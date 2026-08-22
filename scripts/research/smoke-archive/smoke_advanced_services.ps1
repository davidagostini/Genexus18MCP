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
    if ($null -eq $r) {
        Write-Host "[-] [$id] $name - TIMEOUT (${timeoutSec}s)" -ForegroundColor Red
        return $null
    }
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
    Write-Host "[-] [$id] $name - Unexpected response shape" -ForegroundColor Yellow
    return $r
}

Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='adv-smoke'; version='1.0' } } }
$init = ReadId 1 30
Send @{ jsonrpc='2.0'; method='notifications/initialized' }

Write-Host "=== Opening KB ==="
$openKb = CallTool 2 'genexus_kb' @{ action='open'; path='C:\KBs\KBTeste'; alias='KBTeste' } 60

Write-Host "`n=== Testing Advanced Services ==="

# 1. Properties on a Transaction
Write-Host "`n1. genexus_properties (list)"
$props = CallTool 3 'genexus_properties' @{ target='LiveAtomicProbe'; action='list' } 30

# 2. Table Relations
Write-Host "`n2. genexus_analyze (mode=table_relations)"
$tabRel = CallTool 4 'genexus_analyze' @{ mode='table_relations'; target='LiveAtomicProbe' } 30

# 3. KB Stats
Write-Host "`n3. genexus_analyze (mode=kb_stats)"
$kbStats = CallTool 5 'genexus_analyze' @{ mode='kb_stats' } 30

# 4. User Controls list
Write-Host "`n4. genexus_layout (action=list_controls)"
$controls = CallTool 6 'genexus_layout' @{ action='list_controls' } 30

# 5. Native Security Scan
Write-Host "`n5. genexus_security (action=scan_native)"
$secScan = CallTool 7 'genexus_security' @{ action='scan_native' } 60

# 6. Reorg Impact
Write-Host "`n6. genexus_db (action=reorg_impact)"
$reorg = CallTool 8 'genexus_db' @{ action='reorg_impact' } 60

# 7. Comparison / Diff
Write-Host "`n7. genexus_compare"
$comp = CallTool 9 'genexus_compare' @{ objectA='AddDeviceGroups'; objectB='AddDeviceGroups' } 30

try { $p.StandardInput.Close() } catch {}
try { $p.WaitForExit(5000) } catch {}
if (-not $p.HasExited) { $p.Kill() }

Write-Host "`n=== Advanced Services Smoke Completed ===" -ForegroundColor Green
