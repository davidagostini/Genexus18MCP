[CmdletBinding()]
param(
    [string]$Profile,
    [string]$Tool = 'gxhub_status',
    [string]$ArgumentsJson = '{}',
    [switch]$AllowWrite,
    [switch]$ListTools,
    [switch]$PolicyProbe,
    # Caller assertion for PolicyProbe only. The real tools/call path ignores it
    # and uses the catalog readOnlyHint.
    [switch]$ProbeReadOnlyHint,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateRange(1, 3600)]
    [int]$TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CallActionPolicy.ps1')

if ($PolicyProbe) {
    $probePermitted = Test-GxHubManualCallPermitted -ToolName $Tool -ArgumentsJson $ArgumentsJson -ReadOnlyHint:$ProbeReadOnlyHint
    if ($probePermitted) {
        Write-Output 'permitted'
        exit 0
    }
    Write-Output 'allow-write-required'
    exit 2
}

if ([string]::IsNullOrWhiteSpace($Profile)) {
    throw 'Informe -Profile <perfil.json>.'
}
$profilePath = [System.IO.Path]::GetFullPath($Profile)
$publishedExe = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\publish\current\GxMcpHub.exe'))
$builtExe = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\bin\$Configuration\net8.0\GxMcpHub.exe"))
$exe = if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
    $publishedExe
} else {
    $builtExe
}

if (-not (Test-Path -LiteralPath $profilePath)) {
    throw "Perfil nao encontrado: $profilePath"
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "GxMcpHub nao encontrado no publish nem no build $Configuration."
}

try {
    $arguments = $ArgumentsJson | ConvertFrom-Json -AsHashtable -Depth 50
} catch {
    throw "ArgumentsJson invalido: $($_.Exception.Message)"
}
if ($null -eq $arguments) { $arguments = @{} }
if ($arguments -isnot [System.Collections.IDictionary]) {
    throw 'ArgumentsJson deve representar um objeto JSON, por exemplo: {"limit":10}'
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.ArgumentList.Add('--config')
$psi.ArgumentList.Add($profilePath)
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
# O Hub auxiliar sai a cada chamada. Sem isto ele encerraria o Gateway que iniciou, o Worker iria junto e a chamada
# seguinte pagaria de novo a partida a frio da KB (7 a 10 s). Com a chave (Hub 0.5.0 ou mais novo) o Gateway fica e o
# Worker sai sozinho por ociosidade (WorkerIdleTimeoutMinutes, 30 min). O que muda para quem chama:
#   - o Worker segura a KB ate sair (ate 30 min depois da ultima chamada): outra rota ou perfil da MESMA KB nao a abre
#     enquanto isso (dois Workers nao abrem a mesma KB). Quem precisa dela livre chama com
#     GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE=0 ANTES da primeira chamada daquela rota: um Gateway que ja ficou nao e encerrado
#     por uma chamada posterior (o Hub que nao o iniciou nunca o encerra); so sai o Worker, por ociosidade, ou o Gateway,
#     encerrado a mao pelo PID que gxhub_status mostra (gateway.listenerProcessId);
#   - um -TimeoutSec estourado deixa a operacao rodando no Worker: consulte genexus_lifecycle status antes de repetir
#     uma gravacao;
#   - o Gateway que ficou pode ter perfil ou pins mais antigos que o JSON atual (gxhub_status: gateway.profileApplied).
# Um valor que quem chama ja definiu vale: GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE=0 volta ao comportamento antigo. Um Hub
# anterior a 0.5.0 ignora a variavel. A chave vai pelo ambiente DESTE processo, so durante o Start, e e retirada logo
# depois (para nao ficar na sessao de quem chamou este script com &). Nao se usa ProcessStartInfo.Environment: ele
# enumera o ambiente herdado e falha, deixando o bloco pela metade, quando ha duas variaveis que so diferem na caixa.
$keepName = 'GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE'
$keepSetHere = $false
if ($null -eq [Environment]::GetEnvironmentVariable($keepName)) {
    [Environment]::SetEnvironmentVariable($keepName, '1')
    $keepSetHere = $true
}

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $psi
try {
    if (-not $process.Start()) { throw 'Nao foi possivel iniciar o GxMcpHub.' }
} finally {
    if ($keepSetHere) { [Environment]::SetEnvironmentVariable($keepName, $null) }
}

function Send-Request([object]$Body) {
    $line = $Body | ConvertTo-Json -Depth 50 -Compress
    $process.StandardInput.WriteLine($line)
    $process.StandardInput.Flush()
}

function Read-Response([int]$Id, [int]$WaitSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $readTask = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq $readTask) {
            $readTask = $process.StandardOutput.ReadLineAsync()
        }
        if (-not $readTask.Wait(500)) {
            if ($process.HasExited) {
                throw "GxMcpHub encerrou com codigo $($process.ExitCode) antes da resposta $Id."
            }
            continue
        }
        $line = $readTask.GetAwaiter().GetResult()
        $readTask = $null
        if ($null -eq $line) { throw "GxMcpHub encerrou antes da resposta $Id." }
        $message = $line | ConvertFrom-Json -AsHashtable -Depth 100
        if ($message.id -eq $Id) { return $message }
    }
    throw "Timeout de $WaitSeconds segundos aguardando a resposta $Id."
}

try {
    Send-Request @{jsonrpc='2.0';id=1;method='initialize';params=@{
        protocolVersion='2025-11-25'
        capabilities=@{}
        clientInfo=@{name='gxhub-manual';version='1'}
    }}
    $init = Read-Response 1 60
    if ($init.error) { throw ($init.error | ConvertTo-Json -Depth 20 -Compress) }
    Send-Request @{jsonrpc='2.0';method='notifications/initialized';params=@{}}

    Send-Request @{jsonrpc='2.0';id=2;method='tools/list';params=@{}}
    $catalog = Read-Response 2 120
    if ($catalog.error) { throw ($catalog.error | ConvertTo-Json -Depth 20 -Compress) }

    if ($ListTools) {
        $catalog.result.tools |
            Sort-Object name |
            Select-Object name,
                @{name='readOnly';expression={$_.annotations.readOnlyHint}},
                @{name='destructive';expression={$_.annotations.destructiveHint}} |
            Format-Table -AutoSize
        return
    }

    $definition = $catalog.result.tools | Where-Object name -eq $Tool | Select-Object -First 1
    if ($null -eq $definition) {
        throw "Tool nao encontrada: $Tool. Use -ListTools para ver o catalogo."
    }

    $readOnly = $definition.annotations.readOnlyHint -eq $true
    $permittedWithoutAllowWrite = Test-GxHubManualCallPermitted -ToolName $Tool -ArgumentsJson $ArgumentsJson -ReadOnlyHint:$readOnly
    if (-not $permittedWithoutAllowWrite -and -not $AllowWrite) {
        throw "A tool '$Tool' e potencialmente mutavel. Revise os argumentos e repita com -AllowWrite para autorizar a chamada manual."
    }

    Send-Request @{jsonrpc='2.0';id=3;method='tools/call';params=@{
        name=$Tool
        arguments=$arguments
    }}
    $response = Read-Response 3 $TimeoutSec
    if ($response.error) {
        throw ($response.error | ConvertTo-Json -Depth 50)
    }

    foreach ($item in @($response.result.content)) {
        if ($item.type -eq 'text') {
            Write-Output $item.text
        } else {
            Write-Output ($item | ConvertTo-Json -Depth 50)
        }
    }
    if ($response.result.isError) { exit 1 }
}
finally {
    try { $process.StandardInput.Close() } catch {}
    if (-not $process.WaitForExit(15000)) {
        # So o Hub auxiliar: o Gateway que ele iniciou pode estar servindo outra sessao (Hub 0.3.0 o deixa vivo quando ha
        # outro Hub conectado; o 0.5.0 tambem quando esta sozinho, com a chave acima). Matar a arvore derrubaria esse Gateway
        # no meio de uma gravacao; sobrar um Gateway e o erro seguro.
        try { $process.Kill($false) } catch {}
    }
    $process.Dispose()
}
