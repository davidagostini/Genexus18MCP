[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerExe,
    [Parameter(Mandatory)] [string] $GeneXusPath,
    [Parameter(Mandatory)] [string] $KbPath,
    [Parameter(Mandatory)] [switch] $ConfirmDisposableKb
)

$ErrorActionPreference = 'Stop'

function Assert-DisposableKb([string] $Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if ($resolved -notmatch '(?i)[\\/]\.test-kbs[\\/]') {
        throw "Refusing to run the destructive regression outside a .test-kbs directory: $resolved"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'knowledgebase.connection'))) {
        throw "Not a GeneXus KB directory: $resolved"
    }
    return $resolved
}

function Start-TestWorker([string] $Path) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Resolve-Path -LiteralPath $WorkerExe).Path
    $start.Arguments = '--kb "' + $Path + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GX_PROGRAM_DIR'] = (Resolve-Path -LiteralPath $GeneXusPath).Path
    $start.EnvironmentVariables['GX_KB_PATH'] = $Path
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Could not start GxMcp.Worker.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw 'Worker exited before SDK readiness.' }
        if ($line -match 'notifications/worker/sdk_ready') { return $process }
    }
    throw 'Timed out waiting for worker SDK readiness.'
}

function Invoke-Worker($Process, [string] $Id, [string] $Action, [string] $Target,
    [hashtable] $Arguments, [switch] $AllowError) {
    $request = [ordered]@{
        jsonrpc = '2.0'; id = $Id; method = 'Object'; action = $Action
        target = $Target; params = $Arguments
    }
    $Process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress -Depth 20))
    $Process.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $Process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "Worker exited while handling request $Id." }
        try { $response = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ([string] $response.id -ne $Id) { continue }
        if ($null -ne $response.error) {
            throw "Request $Id failed: $($response.error | ConvertTo-Json -Compress -Depth 20)"
        }
        if ([string] $response.result.status -ne 'ok' -and -not $AllowError) {
            throw "Request $Id returned an error: $($response.result | ConvertTo-Json -Compress -Depth 20)"
        }
        return $response.result
    }
    throw "Timed out waiting for request $Id."
}

function Stop-TestWorker($Process) {
    if ($null -eq $Process) { return }
    try { $Process.StandardInput.Close() } catch { }
    if (-not $Process.WaitForExit(15000)) { $Process.Kill() }
    $Process.Dispose()
}

function Assert-ObjectNotFound($Response, [string] $Stage) {
    if ([string] $Response.status -ne 'error' -or [string] $Response.error.code -ne 'ObjectNotFound') {
        throw "$Stage did not return ObjectNotFound: $($Response | ConvertTo-Json -Compress -Depth 20)"
    }
}

function Wait-ObjectNotFound($Process, [string] $Target, [string] $Stage) {
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $response = Invoke-Worker $Process ("verify-" + $attempt) 'Read' $Target @{ type = 'Domain' } -AllowError
        if ([string] $response.error.code -eq 'ObjectNotFound') { return }
        if ([string] $response.error.code -ne 'ObjectNotFoundIndexWarming') {
            Assert-ObjectNotFound $response $Stage
        }
        Start-Sleep -Seconds 1
    }
    throw "$Stage remained in ObjectNotFoundIndexWarming after the native SDK lookup had already confirmed absence."
}

$kb = Assert-DisposableKb $KbPath
$domain = 'McpDeleteDomain' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$worker = $null
$created = $false
try {
    $worker = Start-TestWorker $kb
    Invoke-Worker $worker '1' 'Create' $domain @{
        type = 'Domain'; dataType = 'Character'; length = 40
    } | Out-Null
    $created = $true

    $dryRun = Invoke-Worker $worker '2' 'Delete' $domain @{
        type = 'Domain'; dryRun = $true; confirm = $true
    }
    if ([string] $dryRun.code -ne 'DryRun' -or [string] $dryRun.result.resolvedType -ne 'Domain' -or
        -not [bool] $dryRun.result.wouldDelete -or [bool] $dryRun.result.persisted -or
        [bool] $dryRun.result.mutationDetected -or -not [bool] $dryRun.result.rereadConfirmed -or
        $dryRun.result.implicitLifecycleActions.Count -ne 0 -or $null -eq $dryRun.result.references) {
        throw "Dry-run contract mismatch: $($dryRun | ConvertTo-Json -Compress -Depth 30)"
    }
    $version = [string] $dryRun.result.versionBefore
    if (-not $version) { throw 'Dry-run did not return versionBefore.' }

    $readAfterDryRun = Invoke-Worker $worker '3' 'Read' $domain @{ type = 'Domain' }
    if ([string] $readAfterDryRun.status -ne 'ok') { throw 'Dry-run changed the Domain.' }

    $conflict = Invoke-Worker $worker '4' 'Delete' $domain @{
        type = 'Domain'; dryRun = $false; confirm = $true; expectedVersion = 'stale-version-token'
    } -AllowError
    if ([string] $conflict.error.code -ne 'VersionConflict' -or [bool] $conflict.persisted) {
        throw "Stale token did not fail safely: $($conflict | ConvertTo-Json -Compress -Depth 20)"
    }

    $unconfirmed = Invoke-Worker $worker '5' 'Delete' $domain @{
        type = 'Domain'; dryRun = $false; confirm = $false; expectedVersion = $version
    } -AllowError
    if ([string] $unconfirmed.error.code -ne 'ConfirmRequired') {
        throw 'A real deletion did not require confirm=true.'
    }

    $deleted = Invoke-Worker $worker '6' 'Delete' $domain @{
        type = 'Domain'; dryRun = $false; confirm = $true; expectedVersion = $version
    }
    if ([string] $deleted.code -ne 'ObjectDeleted' -or -not [bool] $deleted.result.persisted -or
        -not [bool] $deleted.result.rereadConfirmed -or [string] $deleted.result.resolvedType -ne 'Domain' -or
        $deleted.result.implicitLifecycleActions.Count -ne 0) {
        throw "Confirmed deletion contract mismatch: $($deleted | ConvertTo-Json -Compress -Depth 30)"
    }
    $created = $false

    Assert-ObjectNotFound (Invoke-Worker $worker '7' 'Read' $domain @{ type = 'Domain' } -AllowError) 'Immediate reread'
    Stop-TestWorker $worker
    $worker = $null

    $worker = Start-TestWorker $kb
    Wait-ObjectNotFound $worker $domain 'Reopen reread'

    [pscustomobject]@{
        Passed = $true
        ResolvedType = [string] $dryRun.result.resolvedType
        ReferencesChecked = $true
        DryRunPersisted = [bool] $dryRun.result.persisted
        ConfirmRequired = $true
        VersionConflictChecked = $true
        DeletePersisted = [bool] $deleted.result.persisted
        RereadConfirmed = [bool] $deleted.result.rereadConfirmed
        ImplicitLifecycleActions = $deleted.result.implicitLifecycleActions.Count
    }
}
finally {
    if ($created -and $null -ne $worker) {
        try { Invoke-Worker $worker 'cleanup' 'Delete' $domain @{ type = 'Domain'; confirm = $true } -AllowError | Out-Null } catch { }
    }
    Stop-TestWorker $worker
}
