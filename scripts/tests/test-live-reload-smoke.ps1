# Permanent live smoke for the reload/lifecycle surface that moved into
# Program.WorkerReload.cs / Program.GatewayTools.cs / Program.LifecycleGateway.cs.
# Exercises, end to end against a real KB: explicit KB open, whoami version
# smoke, lifecycle status + long-poll, soft reload (drain+replace), forced
# alias reload (shared RestoreWorkersAsync core) and the mode=hard guards.
#
# Contract mirrors scripts/test_live_patch_persistence_kbteste.ps1:
# - explicit KB path (no config default, strict resolution policy)
# - isolated log directory via GXMCP_LOG_DIR
# - bounded stdio reads (single outstanding ReadLineAsync, drained into a queue)
# - structured summary JSON + fail-closed exit codes:
#     0 = pass, 1 = fail, 2 = unavailable (no gateway artifact or no KB)

param(
    [string]$KbPath = $env:GXMCP_SMOKE_KB,
    [string]$GxPath = 'C:\Program Files (x86)\GeneXus\GeneXus18',
    [string]$GatewayExe = (Join-Path $PSScriptRoot '..\..\publish\GxMcp.Gateway.exe'),
    [int]$HttpPort = 0
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7+ required. Run with pwsh.' }

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# --- fail-closed availability gate (exit 2 = unavailable) -------------------
if (-not (Test-Path -LiteralPath $GatewayExe -PathType Leaf)) {
    Write-Host 'live=unavailable reason="Gateway artifact not found; run build.ps1 first"'
    exit 2
}
if ([string]::IsNullOrWhiteSpace($KbPath) -or -not (Test-Path -LiteralPath $KbPath -PathType Container)) {
    Write-Host 'live=unavailable reason="KB not found; pass -KbPath or set GXMCP_SMOKE_KB"'
    exit 2
}

$kbAlias = (Split-Path -Leaf $KbPath).ToLowerInvariant()

$httpPort = if ($HttpPort -gt 0) { $HttpPort } else { Get-Random -Minimum 49152 -Maximum 65535 }
$runId = [guid]::NewGuid().ToString('N').Substring(0, 12)
$runDirectory = Join-Path $root ('scratchpad\reload-smoke-' + $runId)
$logDirectory = Join-Path $runDirectory 'gateway-log'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$configPath = Join-Path $runDirectory 'config.json'
$summaryPath = Join-Path $runDirectory 'reload-smoke-summary.json'

# Strict, no default KB: every call carries an explicit kb selector (per AGENTS.md).
@{
    GeneXus     = @{ InstallationPath = $GxPath; WorkerExecutable = (Join-Path $root 'publish\worker\GxMcp.Worker.exe') }
    Server      = @{ HttpPort = $httpPort; McpStdio = $true; BindAddress = '127.0.0.1' }
    Environment = @{ ResolutionPolicy = 'strict' }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8

$env:GX_CONFIG_PATH = $configPath
$env:GXMCP_LOG_DIR = $logDirectory
$env:GX_MCP_PORT = [string]$httpPort
$env:GX_MCP_STDIO = 'true'
Remove-Item Env:\GXMCP_TEST_KB -ErrorAction SilentlyContinue

$errors = New-Object System.Collections.Generic.List[string]
$evidence = [ordered]@{
    schemaVersion  = 'gxmcp-reload-smoke/1'
    kbPath         = $KbPath
    gatewayExe     = $GatewayExe
    httpPort       = $httpPort
    checks         = [ordered]@{}
    errorCount     = 0
    gatewayLogPath = (Join-Path $logDirectory 'gateway_debug.log')
}

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:evidence.checks[$Name] = [ordered]@{ passed = $Passed; detail = $Detail }
    if (-not $Passed) { $script:errors.Add("$Name`: $Detail") | Out-Null }
    $mark = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1} {2} :: {3}" -f (Get-Date -Format 'HH:mm:ss'), $mark, $Name, $Detail)
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $GatewayExe
$psi.WorkingDirectory = Split-Path -Parent $GatewayExe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$gateway = [System.Diagnostics.Process]::new()
$gateway.StartInfo = $psi
[void]$gateway.Start()
$gatewayPid = $gateway.Id
$evidence.gatewayPid = $gatewayPid
Write-Host "Gateway spawned pid=$gatewayPid port=$httpPort"

Start-Sleep -Milliseconds 1200

# At most ONE outstanding ReadLineAsync at a time: the stream rejects
# concurrent reads, so the pending read task is carried across RPC waits in
# the script scope and resumed wherever the next line is needed.
$script:pendingRead = $null
$script:readQueue = [System.Collections.Generic.Queue[string]]::new()

function Read-AvailableLines {
    # Drives the pending read; returns when at least one line is queued or the
    # short poll elapses. Never issues a second read while one is outstanding.
    while ($script:readQueue.Count -eq 0) {
        if ($null -eq $script:pendingRead) { $script:pendingRead = $gateway.StandardOutput.ReadLineAsync() }
        $done = [System.Threading.Tasks.Task]::WhenAny($script:pendingRead, [System.Threading.Tasks.Task]::Delay(200)).GetAwaiter().GetResult()
        if ($done -ne $script:pendingRead) { return }
        $line = $script:pendingRead.GetAwaiter().GetResult()
        $script:pendingRead = $null
        if ($null -eq $line) { throw 'Gateway stdout closed.' }
        $script:readQueue.Enqueue($line)
    }
}

$script:nextId = 1
function Invoke-Rpc {
    param([hashtable]$ToolArgs, [string]$Tool, [int]$TimeoutSec = 240)
    $id = $script:nextId; $script:nextId++
    $req = [ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $Tool; arguments = $ToolArgs } }
    $json = $req | ConvertTo-Json -Depth 12 -Compress
    $gateway.StandardInput.WriteLine($json)
    $gateway.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($gateway.HasExited) { throw "Gateway exited early code=$($gateway.ExitCode)" }
        Read-AvailableLines
        while ($script:readQueue.Count -gt 0) {
            $line = $script:readQueue.Dequeue()
            if ($line -notmatch '"jsonrpc"') { continue }
            try { $resp = $line | ConvertFrom-Json } catch { continue }
            if ($resp.id -eq $id) { return $resp }
        }
    }
    throw "RPC timeout tool=$Tool"
}

function Get-Payload($Resp) {
    $content = $Resp.result.content
    if ($content -and $content.Count -gt 0) {
        try { return $content[0].text | ConvertFrom-Json } catch { return $null }
    }
    return $null
}

# Cleanup on terminating errors, per docs/live-kb-test-harness.md.
trap {
    try { if ($gateway -and -not $gateway.HasExited) { $gateway.StandardInput.Close() } } catch {}
    Start-Sleep -Milliseconds 1500
    try { if ($gateway -and -not $gateway.HasExited) { $gateway.Kill($true) } catch { try { $gateway.Kill() } catch {} } } catch {}
    try { $gateway.Dispose() } catch {}
    throw
}

try {
    # --- initialize -------------------------------------------------------
    $initReq = [ordered]@{ jsonrpc = '2.0'; id = 0; method = 'initialize'; params = [ordered]@{
        protocolVersion = '2024-11-05'; capabilities = [ordered]@{}; clientInfo = [ordered]@{ name = 'reload-smoke'; version = '1.0' } } }
    $gateway.StandardInput.WriteLine(($initReq | ConvertTo-Json -Depth 8 -Compress)); $gateway.StandardInput.Flush()
    Start-Sleep -Milliseconds 800

    # --- 1. open the KB explicitly ---------------------------------------
    $openResp = Invoke-Rpc -Tool 'genexus_kb' -ToolArgs @{ action = 'open'; kb = $kbAlias; path = $KbPath }
    $openPayload = Get-Payload $openResp
    $workerPid1 = $openPayload.workerPid
    Add-Check 'kb_open' ($null -ne $workerPid1 -and $openPayload.opened -eq $kbAlias) "workerPid=$workerPid1 opened=$($openPayload.opened)"

    # --- 2. whoami version smoke -----------------------------------------
    # Contract: whoami.kb.active carries the selected alias,
    # whoami.worker.pid the worker process id, geneXus.{versionMatches,matchedMajor}
    # the SDK compatibility smoke.
    $whoResp = Invoke-Rpc -Tool 'genexus_whoami' -ToolArgs @{ kb = $kbAlias; verbose = $true }
    $whoPayload = Get-Payload $whoResp
    $gx = $whoPayload.geneXus
    $activeKb = $whoPayload.kb.active
    $workerPid1 = $whoPayload.worker.pid
    Add-Check 'whoami_version_smoke' (
        $null -ne $gx -and $gx.versionMatches -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$gx.matchedMajor) -and $activeKb -eq $kbAlias
    ) "versionMatches=$($gx.versionMatches) matchedMajor=$($gx.matchedMajor) activeKb=$activeKb workerPid=$workerPid1"

    # --- 3. lifecycle status baseline -------------------------------------
    # Contract (BuildService): _meta.snapshot exists on a build-task status
    # response. With no active build task the status is idle — assert the
    # idle envelope instead of a build baseline.
    $status1 = Get-Payload (Invoke-Rpc -Tool 'genexus_lifecycle' -ToolArgs @{ action = 'status'; kb = $kbAlias })
    Add-Check 'lifecycle_status_baseline' ($null -ne $status1) "idle status=$($status1.status) taskId=$($status1.taskId)"

    # --- 4. lifecycle long-poll (wait_seconds=5; idle status returns immediately)
    $pollSw = [System.Diagnostics.Stopwatch]::StartNew()
    $status2 = Get-Payload (Invoke-Rpc -Tool 'genexus_lifecycle' -ToolArgs @{ action = 'status'; kb = $kbAlias; wait_seconds = 5 })
    $pollSw.Stop()
    Add-Check 'lifecycle_longpoll' ($null -ne $status2 -and $pollSw.ElapsedMilliseconds -lt 30000) "returned in $($pollSw.ElapsedMilliseconds)ms (wait_seconds honored without prior snapshot)"

    # --- 5. soft reload (graceful drain + replace) ------------------------
    $softResp = Invoke-Rpc -Tool 'genexus_worker_reload' -ToolArgs @{ mode = 'soft'; kb = $kbAlias } -TimeoutSec 300
    $softPayload = Get-Payload $softResp
    Add-Check 'reload_soft' ($softPayload.status -eq 'Reloaded' -and $softPayload.swappedAndReady -eq $true) "status=$($softPayload.status) swappedAndReady=$($softPayload.swappedAndReady)"

    # reconnect once per playbook if pipe reports stale worker; verify new pid via whoami
    $whoResp2 = Invoke-Rpc -Tool 'genexus_whoami' -ToolArgs @{ kb = $kbAlias }
    $whoPayload2 = Get-Payload $whoResp2
    $workerPid2 = $whoPayload2.worker.pid
    Add-Check 'reload_soft_new_worker_pid' ($null -ne $workerPid2 -and $workerPid2 -ne $workerPid1) "old=$workerPid1 new=$workerPid2"

    # --- 6. force reload scoped to alias (decomposed force path) ----------
    $forceResp = Invoke-Rpc -Tool 'genexus_worker_reload' -ToolArgs @{ mode = 'soft'; force = $true; alias = $kbAlias } -TimeoutSec 300
    $forcePayload = Get-Payload $forceResp
    $restored = @($forcePayload.restoredWorkers)
    Add-Check 'reload_force_alias' (
        $forcePayload.status -eq 'Forced' -and $forcePayload.scope -eq 'alias' -and $restored.Count -eq 1 -and $restored[0] -eq $kbAlias
    ) "status=$($forcePayload.status) scope=$($forcePayload.scope) restored=[$($restored -join ',')] failed=[$(@($forcePayload.failedWorkers) -join ',')]"

    # --- 7. force reload validation guards (schema order: mode=hard without
    # sourceDir is rejected before the force+hard combination) --------------
    $guardResp = Invoke-Rpc -Tool 'genexus_worker_reload' -ToolArgs @{ mode = 'hard'; kb = $kbAlias }
    $guardPayload = Get-Payload $guardResp
    Add-Check 'reload_guard_hard_source_required' ($guardPayload.error.code -eq 'ReloadSourceRequired') "code=$($guardPayload.error.code)"

    $guard2Resp = Invoke-Rpc -Tool 'genexus_worker_reload' -ToolArgs @{ mode = 'hard'; force = $true; sourceDir = (Join-Path $root 'publish\worker'); kb = $kbAlias }
    $guard2Payload = Get-Payload $guard2Resp
    Add-Check 'reload_guard_force_hard' ($guard2Payload.error.code -eq 'ForceHardReloadUnsupported') "code=$($guard2Payload.error.code)"

    # --- 8. kb list carries the post-reload state -------------------------
    $listResp = Invoke-Rpc -Tool 'genexus_kb' -ToolArgs @{ action = 'list'; kb = $kbAlias }
    $listPayload = Get-Payload $listResp
    $openCount = @($listPayload.openKbs).Count
    Add-Check 'kb_list_after_reload' ($openCount -ge 1) "openKbs=$openCount"

    # --- 9. lifecycle status after reloads (index mirror intact) ----------
    $status3 = Get-Payload (Invoke-Rpc -Tool 'genexus_lifecycle' -ToolArgs @{ action = 'status'; kb = $kbAlias })
    Add-Check 'lifecycle_status_after_reload' ($null -ne $status3) "status=$($status3.status)"
}
catch {
    $errors.Add("fatal: $($_.Exception.Message)") | Out-Null
    Add-Check 'fatal_error' $false $_.Exception.Message
}
finally {
    $evidence.errorCount = $errors.Count
    try { $gateway.StandardInput.Close() } catch {}
    Start-Sleep -Milliseconds 1500
    if (-not $gateway.HasExited) { try { $gateway.Kill($true) } catch { try { $gateway.Kill() } catch {} } }
    $gateway.Dispose()
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "`nSummary: $summaryPath"
    Get-Content -LiteralPath $summaryPath | ForEach-Object { Write-Host "  $_" }
}

if ($errors.Count -gt 0) {
    Write-Error "reload/lifecycle smoke FAILED with $($errors.Count) error(s)."
    exit 1
}
Write-Host 'reload/lifecycle smoke PASSED (live=pass).'
exit 0
