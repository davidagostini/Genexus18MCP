$ErrorActionPreference = 'Stop'
$gw = (Resolve-Path 'publish\GxMcp.Gateway.exe').Path

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gw
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $false
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['GX_CONFIG_PATH'] = (Resolve-Path 'config.json').Path
$psi.EnvironmentVariables['GX_MCP_STDIO']   = 'true'
$psi.EnvironmentVariables['GX_PROGRAM_DIR'] = 'C:\Program Files (x86)\GeneXus\GeneXus18'

$p = [System.Diagnostics.Process]::Start($psi)

function Send($obj) { 
    $p.StandardInput.WriteLine( ($obj | ConvertTo-Json -Compress -Depth 12) )
    $p.StandardInput.Flush() 
}

function ReadId([int]$id, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $line = $p.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ($line -notmatch '^\s*\{') { continue }
        try { 
            $j = $line | ConvertFrom-Json
            if ($j.id -eq $id) { return $j }
        } catch { continue }
    }
    return $null
}

function CallTool($id, $name, $toolArgs, $timeoutSec = 30) {
    Send @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$name; arguments=$toolArgs } }
    $r = ReadId $id $timeoutSec
    if ($r.error) {
        Write-Host "[-] [$id] $name - ERROR: $($r.error.message) ($($r.error.code))" -ForegroundColor Red
        return $r.error
    }
    if ($r.result -and $r.result.content -and $r.result.content.Count -gt 0) {
        $txt = $r.result.content[0].text
        if ($txt) {
            try {
                $parsed = $txt | ConvertFrom-Json
                Write-Host "[+] [$id] $name - OK" -ForegroundColor Green
                return $parsed
            } catch {
                Write-Host "[+] [$id] $name - Returned text" -ForegroundColor Green
                return $txt
            }
        }
    }
    Write-Host "[-] [$id] $name - Unexpected response shape: $($r | ConvertTo-Json -Compress)" -ForegroundColor Yellow
    return $r
}

Write-Host "=== 1. Initialize ==="
Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='smoke-validator'; version='1.0' } } }
$init = ReadId 1 30
if ($init) {
    Write-Host "[+] Initialize successful. Server: $($init.result.serverInfo.name) v$($init.result.serverInfo.version)" -ForegroundColor Green
} else {
    Write-Host "[-] Initialize failed." -ForegroundColor Red
    exit 1
}

Send @{ jsonrpc='2.0'; method='notifications/initialized' }

Write-Host "`n=== 2. Tools List ==="
Send @{ jsonrpc='2.0'; id=2; method='tools/list'; params=@{} }
$toolsList = ReadId 2 30
if ($toolsList) {
    $toolCount = $toolsList.result.tools.Count
    Write-Host "[+] tools/list returned $toolCount tools." -ForegroundColor Green
}

Write-Host "`n=== 3. Open KB ==="
$openKb = CallTool 3 'genexus_kb' @{ action='open'; path='C:\KBs\KBTeste'; alias='KBTeste' } 60

Write-Host "`n=== 4. Whoami & Health ==="
$whoami = CallTool 4 'genexus_whoami' @{ kb='KBTeste' } 60
if ($whoami) {
    Write-Host "    KB: $($whoami.kb.name) ($($whoami.kb.path))"
    Write-Host "    GX: $($whoami.geneXus.installationPath) (v$($whoami.geneXus.version))"
}

Write-Host "`n=== 5. List Procedures & Transactions ==="
$procs = CallTool 5 'genexus_list_objects' @{ typeFilter='Procedure'; limit=5 } 60
$txs = CallTool 52 'genexus_list_objects' @{ typeFilter='Transaction'; limit=5 } 60

$sampleProc = if ($procs -and $procs.results -and $procs.results.Count -gt 0) { $procs.results[0].name } else { $null }
$sampleTx = if ($txs -and $txs.results -and $txs.results.Count -gt 0) { $txs.results[0].name } else { $null }

Write-Host "Sample Procedure: $sampleProc"
Write-Host "Sample Transaction: $sampleTx"

if ($sampleProc) {
    Write-Host "`n=== 6. Inspect & Read Procedure: $sampleProc ==="
    $inspect = CallTool 6 'genexus_inspect' @{ name=$sampleProc } 60
    $read = CallTool 61 'genexus_read' @{ name=$sampleProc } 60
    if ($read) {
        Write-Host "    Parts: $($read.parts -join ', ')"
        Write-Host "    Source lines: $(if ($read.source) { ($read.source -split "`n").Count } else { 0 })"
        Write-Host "    VersionToken: $($read.versionToken)"
    }

    Write-Host "`n=== 7. Analyze Procedure: $sampleProc ==="
    $linter = CallTool 7 'genexus_analyze' @{ mode='linter'; target=$sampleProc } 60
    $summary = CallTool 71 'genexus_analyze' @{ mode='summary'; target=$sampleProc } 60

    Write-Host "`n=== 8. Read Variables of Procedure: $sampleProc ==="
    $vars = CallTool 8 'genexus_variable' @{ action='list'; target=$sampleProc } 60
    if ($vars -and $vars.variables) {
        Write-Host "    Variables count: $($vars.variables.Count)"
    }
}

if ($sampleTx) {
    Write-Host "`n=== 9. Structure of Transaction: $sampleTx ==="
    $struct = CallTool 9 'genexus_structure' @{ action='read'; target=$sampleTx } 60
    if ($struct -and $struct.attributes) {
        Write-Host "    Attributes count: $($struct.attributes.Count)"
    }
}

Write-Host "`n=== 9. Doctor & System Health ==="
$doctor = CallTool 9 'genexus_doctor' @{} 60

Write-Host "`n=== 10. Close Session ==="
try { $p.StandardInput.Close() } catch {}
try { $p.WaitForExit(5000) } catch {}
if (-not $p.HasExited) { $p.Kill() }

Write-Host "`n=== Live Smoke Testing Completed Successfully! ===" -ForegroundColor Green
