param(
    [Parameter(Mandatory)][string]$Profile,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$LiveWhoAmI,
    [switch]$NoBuild,
    [ValidateRange(0, 300)]
    [int]$HoldOpenSeconds = 0
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\GxMcpHub.csproj'
$publishDirectory = Join-Path $PSScriptRoot '..\publish\current'
$exe = Join-Path $publishDirectory 'GxMcpHub.exe'
$profilePath = [System.IO.Path]::GetFullPath($Profile)
$profileConfig = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$expectedKbName = Split-Path -Leaf $profileConfig.knowledgeBasePath
$writeMode = $profileConfig.writePolicy.mode
if (-not $NoBuild) {
    & dotnet publish $project -c $Configuration --nologo `
        "-p:OutputPath=$publishDirectory" `
        "-p:PublishDir=$publishDirectory"
    if ($LASTEXITCODE -ne 0) { throw 'Falha no publish do smoke.' }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Executável publicado não encontrado: $exe"
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.ArgumentList.Add('--config')
$psi.ArgumentList.Add($profilePath)
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $psi
if (-not $process.Start()) { throw 'Não foi possível iniciar o hub.' }

function Send-Request([object]$Body) {
    $line = $Body | ConvertTo-Json -Depth 30 -Compress
    $process.StandardInput.WriteLine($line)
    $process.StandardInput.Flush()
}

function Read-Response([int]$Id, [int]$TimeoutSec = 120) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $readTask = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq $readTask) {
            $readTask = $process.StandardOutput.ReadLineAsync()
        }
        if (-not $readTask.Wait(1000)) { continue }
        $line = $readTask.GetAwaiter().GetResult()
        $readTask = $null
        if ($null -eq $line) { throw "Hub encerrou antes da resposta $Id." }
        $message = $line | ConvertFrom-Json -Depth 50
        if ($message.id -eq $Id) { return $message }
    }
    throw "Timeout aguardando resposta $Id."
}

try {
    Send-Request @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='smoke';version='1'}}}
    $init = Read-Response 1 30
    if ($init.result.serverInfo.name -ne 'GxMcpHub') { throw 'initialize inválido.' }

    Send-Request @{jsonrpc='2.0';method='notifications/initialized';params=@{}}
    Send-Request @{jsonrpc='2.0';id=2;method='tools/list';params=@{}}
    $list = Read-Response 2 60
    $names = @($list.result.tools | ForEach-Object name)
    foreach ($required in 'gxhub_status','gxhub_refresh','native_genexus_whoami') {
        if ($required -notin $names) { throw "Tool esperada ausente: $required" }
    }
    if ($writeMode -eq 'read-only' -and 'native_genexus_edit' -in $names) {
        throw 'Política read-only falhou: native_genexus_edit foi exposta.'
    }
    if ($writeMode -in @('single-writer', 'open-writer') -and 'native_genexus_edit' -notin $names) {
        throw "Política $writeMode falhou: native_genexus_edit não foi exposta."
    }
    if ($writeMode -notin @('read-only', 'single-writer', 'open-writer')) {
        throw "Política desconhecida no smoke: $writeMode."
    }
    $generic = $list.result.tools | Where-Object name -eq 'gxhub_call' | Select-Object -First 1
    if ($null -eq $generic -or $generic.annotations.readOnlyHint -ne $false -or
        $generic.annotations.destructiveHint -ne $true) {
        throw 'gxhub_call não foi anunciada conservadoramente ao cliente MCP.'
    }

    Send-Request @{jsonrpc='2.0';id=3;method='tools/call';params=@{name='gxhub_status';arguments=@{}}}
    $status = Read-Response 3 30
    if ($status.result.isError) { throw 'gxhub_status retornou erro.' }

    Send-Request @{jsonrpc='2.0';id=4;method='tools/call';params=@{name='gxhub_refresh';arguments=@{}}}
    $refresh = Read-Response 4 120
    if ($refresh.result.isError) { throw 'gxhub_refresh retornou erro.' }

    if ($writeMode -eq 'read-only') {
        Send-Request @{jsonrpc='2.0';id=5;method='tools/call';params=@{name='gxhub_call';arguments=@{backend='native';tool='genexus_edit';arguments=@{}}}}
        $blocked = Read-Response 5 30
        if (-not $blocked.result.isError -or $blocked.result.content[0].text -notmatch 'POLÍTICA') {
            throw 'A chamada genérica não bloqueou uma escrita fora da política.'
        }
    }

    if ($LiveWhoAmI) {
        Send-Request @{jsonrpc='2.0';id=6;method='tools/call';params=@{name='native_genexus_whoami';arguments=@{}}}
        $who = Read-Response 6 240
        if ($who.result.isError) { throw 'native_genexus_whoami retornou erro.' }
        if ($who.result.content[0].text -notmatch [regex]::Escape($expectedKbName)) {
            throw "whoami não confirmou a KB $expectedKbName."
        }
    }

    Write-Host "Smoke aprovado: initialize, tools/list, política $writeMode, status e refresh$($(if($LiveWhoAmI){', whoami real'}else{''}))."
    if ($HoldOpenSeconds -gt 0) {
        Write-Host "Mantendo a instância aberta por $HoldOpenSeconds segundos para validação concorrente."
        Start-Sleep -Seconds $HoldOpenSeconds
    }
}
finally {
    try { $process.StandardInput.Close() } catch {}
    if (-not $process.WaitForExit(15000)) {
        try { $process.Kill($true) } catch {}
    }
    $process.Dispose()
}

# O backend filho é encerrado deliberadamente pelo Hub e pode registrar -1.
# Isso não deve transformar um smoke aprovado em falha do processo chamador.
$global:LASTEXITCODE = 0
