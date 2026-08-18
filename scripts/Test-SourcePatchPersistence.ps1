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
    $allowedRoots = @('..\.test-kbs', '..\..\.test-kbs', '..\..\..\.test-kbs') |
        ForEach-Object { Join-Path $PSScriptRoot $_ } |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { (Resolve-Path -LiteralPath $_).Path }
    $allowed = $allowedRoots | Where-Object {
        $resolved.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if (-not $allowed) {
        throw "Refusing to modify a KB outside this workspace's .test-kbs directories."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'knowledgebase.connection'))) {
        throw "Not a GeneXus KB directory: $resolved"
    }
    return $resolved
}

function Start-TestWorker([string] $ResolvedKbPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Resolve-Path -LiteralPath $WorkerExe).Path
    $start.Arguments = '--kb "' + $ResolvedKbPath + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GX_PROGRAM_DIR'] = (Resolve-Path -LiteralPath $GeneXusPath).Path
    $start.EnvironmentVariables['GX_KB_PATH'] = $ResolvedKbPath
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

function Stop-TestWorker($Process) {
    if ($null -eq $Process) { return }
    try { $Process.StandardInput.Close() } catch { }
    if (-not $Process.WaitForExit(15000)) { $Process.Kill() }
    $Process.Dispose()
}

function Invoke-Worker(
    $Process,
    [string] $Id,
    [string] $Method,
    [string] $Action,
    [string] $Target,
    [hashtable] $Arguments,
    [string] $Payload = $null,
    [switch] $AllowServiceError
) {
    $request = [ordered]@{
        jsonrpc = '2.0'; id = $Id; method = $Method; action = $Action
        target = $Target; params = $Arguments
    }
    if ($null -ne $Payload) { $request.payload = $Payload }
    $Process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress -Depth 20))
    $Process.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $Process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "Worker exited while handling request $Id." }
        try { $response = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ([string] $response.id -ne $Id) { continue }
        if ($null -ne $response.error) {
            throw "Request $Id failed at the transport layer: $($response.error | ConvertTo-Json -Compress -Depth 20)"
        }
        if ([string] $response.result.status -eq 'error' -and -not $AllowServiceError) {
            throw "Request $Id failed: $($response.result | ConvertTo-Json -Compress -Depth 20)"
        }
        return $response.result
    }
    throw "Timed out waiting for request $Id."
}

function Get-Source($ReadResult) {
    $source = $ReadResult.source
    if ($null -eq $source) { $source = $ReadResult.result.source }
    if ($null -eq $source) { throw 'Read response did not contain Source.' }
    return [string] $source
}

function Get-VersionToken($Result) {
    $token = $Result.versionToken
    if ($null -eq $token) { $token = $Result.result.versionToken }
    if ([string]::IsNullOrWhiteSpace([string] $token)) { throw 'Response did not contain versionToken.' }
    return [string] $token
}

function Assert-PatchReceipt($Result) {
    if ([string] $Result.code -ne 'Applied') { throw "Expected Applied, got '$($Result.code)'." }
    $receipt = $Result.result
    foreach ($flag in @('saved', 'persisted', 'verified', 'reReadConfirmed')) {
        if (-not [bool] $receipt.$flag) { throw "Patch receipt did not confirm $flag." }
    }
    if ([string] $receipt.verification.source -ne 'fresh-sdk-read') {
        throw 'Patch verification did not identify a fresh SDK read.'
    }
    foreach ($state in @('requested', 'saved', 'reRead')) {
        if ([string]::IsNullOrWhiteSpace([string] $receipt.content.$state.hash)) {
            throw "Patch receipt is missing the $state content hash."
        }
    }
    if ([string] $receipt.content.requested.hash -ne [string] $receipt.content.saved.hash -or
        [string] $receipt.content.saved.hash -ne [string] $receipt.content.reRead.hash) {
        throw 'Requested, saved, and independently re-read Source hashes differ.'
    }
    if (@($receipt.implicitOperations).Count -ne 0) {
        throw 'Patch unexpectedly reported an implicit lifecycle operation.'
    }
}

$resolvedKb = Assert-DisposableKb $KbPath
$worker = $null
$created = $false
$suffix = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$procedure = 'McpSourcePersistence' + $suffix
$initial = "// MCP_SOURCE_SEED_$suffix`r`n// MCP_SLOT_1`r`n// MCP_SLOT_2`r`n// MCP_SLOT_3"
$tokens = [Collections.Generic.List[string]]::new()

try {
    $worker = Start-TestWorker $resolvedKb
    Invoke-Worker $worker '1' 'Object' 'Create' $procedure @{ type = 'Procedure' } | Out-Null
    $created = $true
    Invoke-Worker $worker '2' 'Write' 'Source' $procedure @{ type = 'Procedure'; part = 'Source' } $initial | Out-Null

    $read = Invoke-Worker $worker '3' 'Read' 'ExtractSource' $procedure @{ type = 'Procedure'; part = 'Source' }
    $token = Get-VersionToken $read
    [void] $tokens.Add($token)

    # Full Source preview: this is the regression gate for a dry-run that used to
    # enter Write/Source asynchronously and could call Save or wedge the STA worker.
    $fullPreview = $initial + "`r`n// MCP_FULL_PREVIEW_MUST_NOT_PERSIST"
    $staleFullDry = Invoke-Worker $worker 'full-dry-stale' 'Write' 'Source' $procedure @{
        type = 'Procedure'; part = 'Source'; mode = 'full'; dryRun = $true
        expectedVersion = 'stale-version'; rollbackOnFailure = $true
    } $fullPreview -AllowServiceError
    if ([string] $staleFullDry.error.code -ne 'StaleObject') {
        throw "Full dry-run did not enforce expectedVersion: $($staleFullDry | ConvertTo-Json -Compress -Depth 20)"
    }
    $fullDry = Invoke-Worker $worker 'full-dry' 'Write' 'Source' $procedure @{
        type = 'Procedure'; part = 'Source'; mode = 'full'; dryRun = $true
        expectedVersion = $token; rollbackOnFailure = $true
    } $fullPreview
    if ([string] $fullDry.code -ne 'WriteDryRun' -or [bool] $fullDry.persisted) {
        throw "Full dry-run did not return WriteDryRun with persisted=false: $($fullDry | ConvertTo-Json -Compress -Depth 20)"
    }
    if ([bool] $fullDry.mutationDetected -or @($fullDry.implicitLifecycleActions).Count -ne 0) {
        throw 'Full dry-run reported a mutation or an implicit lifecycle action.'
    }
    $afterFullDry = Invoke-Worker $worker 'after-full-dry' 'Read' 'ExtractSource' $procedure @{ type = 'Procedure'; part = 'Source' }
    if ((Get-Source $afterFullDry) -ne $initial -or (Get-VersionToken $afterFullDry) -ne $token) {
        throw 'Full dry-run changed Source or its version token.'
    }

    # A real full write must return the independently re-read Source and version,
    # without running Specify/Generate/Build/Compile or any other lifecycle action.
    $fullPersisted = $initial + "`r`n// MCP_FULL_REAL"
    $fullWrite = Invoke-Worker $worker 'full-real' 'Write' 'Source' $procedure @{
        type = 'Procedure'; part = 'Source'; mode = 'full'; dryRun = $false
        expectedVersion = $token; rollbackOnFailure = $true
    } $fullPersisted
    if (-not [bool] $fullWrite.persisted -or -not [bool] $fullWrite.postSaveVerification.reReadConfirmed) {
        throw "Full write did not confirm persistence and read-back: $($fullWrite | ConvertTo-Json -Compress -Depth 20)"
    }
    if ([string] $fullWrite.source -ne $fullPersisted -or @($fullWrite.implicitLifecycleActions).Count -ne 0) {
        throw 'Full write did not return the exact persisted Source or reported an implicit lifecycle action.'
    }
    $afterFullWrite = Invoke-Worker $worker 'after-full-real' 'Read' 'ExtractSource' $procedure @{ type = 'Procedure'; part = 'Source' }
    if ((Get-Source $afterFullWrite) -ne $fullPersisted) { throw 'Independent read differs from the full-write Source.' }
    $token = Get-VersionToken $afterFullWrite
    [void] $tokens.Add($token)

    $dry = Invoke-Worker $worker '4' 'Patch' 'Apply' $procedure @{
        type = 'Procedure'; part = 'Source'; operation = 'Replace'; context = '// MCP_SLOT_1'
        expectedCount = 1; dryRun = $true; baseVersion = $token; rollbackOnFailure = $true
        verifyRollback = $true; verifyMode = 'exact'
    } "// MCP_SLOT_1`r`n// MCP_APPLIED_1"
    if ([string] $dry.code -ne 'Applied' -or [bool] $dry.result.saved) {
        throw 'Dry-run patch did not report Applied with saved=false.'
    }
    if ((Get-Source (Invoke-Worker $worker '5' 'Read' 'ExtractSource' $procedure @{ type = 'Procedure'; part = 'Source' })) -match 'MCP_APPLIED_1') {
        throw 'Dry-run patch changed Source.'
    }

    for ($i = 1; $i -le 3; $i++) {
        $slot = "// MCP_SLOT_$i"
        $replacement = "$slot`r`n// MCP_APPLIED_$i"
        $patch = Invoke-Worker $worker ("patch-$i") 'Patch' 'Apply' $procedure @{
            type = 'Procedure'; part = 'Source'; operation = 'Replace'; context = $slot
            expectedCount = 1; dryRun = $false; baseVersion = $token; rollbackOnFailure = $true
            verifyRollback = $true; verifyMode = 'exact'
        } $replacement
        Assert-PatchReceipt $patch
        $token = Get-VersionToken $patch
        [void] $tokens.Add($token)

        $freshRead = Invoke-Worker $worker ("read-$i") 'Read' 'ExtractSource' $procedure @{ type = 'Procedure'; part = 'Source' }
        $freshSource = Get-Source $freshRead
        for ($expected = 1; $expected -le $i; $expected++) {
            if ($freshSource -notmatch "MCP_APPLIED_$expected") {
                throw "Independent read after patch $i lost patch $expected."
            }
        }
        if ((Get-VersionToken $freshRead) -ne $token) {
            throw "Patch $i returned a token different from the independently read Source."
        }
    }

    $stale = Invoke-Worker $worker 'stale' 'Patch' 'Apply' $procedure @{
        type = 'Procedure'; part = 'Source'; operation = 'Replace'; context = '// MCP_SLOT_3'
        expectedCount = 1; dryRun = $false; baseVersion = $tokens[0]; rollbackOnFailure = $true
        verifyRollback = $true; verifyMode = 'exact'
    } "// MCP_SLOT_3`r`n// MUST_NOT_PERSIST" -AllowServiceError
    if ([string] $stale.error.code -ne 'VersionConflict') {
        throw "Stale token did not return VersionConflict: $($stale | ConvertTo-Json -Compress -Depth 20)"
    }
    $afterConflict = Get-Source (Invoke-Worker $worker 'after-conflict' 'Read' 'ExtractSource' $procedure @{ type = 'Procedure'; part = 'Source' })
    if ($afterConflict -match 'MUST_NOT_PERSIST') { throw 'VersionConflict changed Source.' }

    [pscustomobject]@{
        Passed = $true
        PatchesApplied = 3
        FreshReadsConfirmed = 3
        FullDryRunConfirmed = $true
        FullWriteReadBackConfirmed = $true
        StaleTokenRejected = $true
        LifecycleOperations = 0
        Procedure = $procedure
        KbPath = $resolvedKb
    }
}
finally {
    if ($created -and $null -ne $worker) {
        try { Invoke-Worker $worker 'cleanup' 'Object' 'Delete' $procedure @{ type = 'Procedure'; confirm = $true } | Out-Null } catch { Write-Warning $_ }
    }
    Stop-TestWorker $worker
}
