[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerExe,
    [Parameter(Mandatory)] [string] $GeneXusPath,
    [Parameter(Mandatory)] [string] $KbPath,
    [Parameter(Mandatory)] [switch] $ConfirmDisposableKb
)

$ErrorActionPreference = 'Stop'

function Start-TestWorker {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Resolve-Path -LiteralPath $WorkerExe).Path
    $start.Arguments = '--kb "' + (Resolve-Path -LiteralPath $KbPath).Path + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GX_PROGRAM_DIR'] = (Resolve-Path -LiteralPath $GeneXusPath).Path
    $start.EnvironmentVariables['GX_KB_PATH'] = (Resolve-Path -LiteralPath $KbPath).Path
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Could not start the GeneXus Worker.' }
    while ($true) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw 'Worker exited before SDK readiness.' }
        if ($line -match 'notifications/worker/sdk_ready') { return $process }
    }
}

function Invoke-Worker($Process, [string] $Id, [string] $Module, [string] $Action,
    [string] $Target, [hashtable] $Arguments, [string] $Payload = $null, [switch] $AllowError) {
    $request = [ordered]@{
        jsonrpc = '2.0'; id = $Id; method = $Module; action = $Action
        target = $Target; params = $Arguments
    }
    if ($null -ne $Payload) { $request.payload = $Payload }
    $Process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress -Depth 30))
    $Process.StandardInput.Flush()
    while ($true) {
        $line = $Process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "Worker exited while handling request $Id." }
        try { $response = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ([string] $response.id -ne $Id) { continue }
        if ($null -ne $response.error) { throw "RPC $Id failed: $($response.error.message)" }
        if ([string] $response.result.status -ne 'ok' -and -not $AllowError) {
            throw "Request $Id failed: $($response.result | ConvertTo-Json -Compress -Depth 30)"
        }
        return $response.result
    }
}

function Stop-TestWorker($Process) {
    if ($null -eq $Process) { return }
    try { $Process.StandardInput.Close() } catch { }
    if (-not $Process.WaitForExit(15000)) { $Process.Kill() }
    $Process.Dispose()
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$resolvedKb = (Resolve-Path -LiteralPath $KbPath).Path
if ($resolvedKb -notmatch '(?i)[\\/]\.test-kbs[\\/]') {
    throw "Refusing to mutate a KB outside a .test-kbs directory: $resolvedKb"
}
if (-not (Test-Path -LiteralPath (Join-Path $resolvedKb 'knowledgebase.connection'))) {
    throw "Not a GeneXus KB: $resolvedKb"
}

$suffix = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$transactions = @(
    "McpIndexQueue$suffix"
    "McpIndexStarted$suffix"
    "McpIndexCreated$suffix"
    "McpIndexSchedule$suffix"
)
$main = $transactions[0]
$attributes = @($transactions | ForEach-Object { $_ + 'Id' })
$indexName = 'UQueuePending' + $suffix
$secondIndexName = 'UQueuePendingConcurrent' + $suffix
$worker = $null
$indexCreated = $false

try {
    $worker = Start-TestWorker
    for ($i = 0; $i -lt $transactions.Count; $i++) {
        Invoke-Worker $worker "create-$i" 'Object' 'Create' $transactions[$i] @{ type = 'Transaction' } | Out-Null
    }

    $children = @()
    for ($i = 0; $i -lt $attributes.Count; $i++) {
        $children += [ordered]@{ name = $attributes[$i]; isKey = ($i -eq 0) }
    }
    $structurePayload = @{ children = $children } | ConvertTo-Json -Compress -Depth 10
    Invoke-Worker $worker 'structure' 'Structure' 'UpdateVisualStructure' $main @{} $structurePayload | Out-Null

    $before = Invoke-Worker $worker 'before' 'Structure' 'GetVisualIndexes' $main @{}
    $beforeToken = [string] $before.result.versionToken
    Assert-True ($before.result.indexes.name -notcontains $indexName) 'The test index already existed before dry-run.'

    $indexPayload = @{
        name = $indexName; unique = $false; attributes = $attributes; order = 'Ascending'
    } | ConvertTo-Json -Compress -Depth 10
    $preview = Invoke-Worker $worker 'preview' 'Structure' 'CreateIndex' $main @{
        dryRun = $true; baseVersion = $beforeToken; rollbackOnFailure = $true
    } $indexPayload
    Assert-True ([string] $preview.code -eq 'IndexCreatePreview') 'Dry-run did not return IndexCreatePreview.'
    Assert-True (-not [bool] $preview.result.persisted) 'Dry-run reported persisted=true.'
    Assert-True (-not [bool] $preview.result.saved) 'Dry-run reported saved=true.'
    Assert-True ([bool] $preview.result.verified) 'Dry-run state verification failed.'
    Assert-True ([bool] $preview.result.versionUnchanged) 'Dry-run changed the version token.'
    Assert-True ([string] $preview.result.versionToken -eq $beforeToken) 'Dry-run returned a different token.'
    Assert-True ($preview.result.implicitOperations.Count -eq 0) 'Dry-run reported implicit lifecycle operations.'

    Stop-TestWorker $worker
    $worker = Start-TestWorker
    $afterPreview = Invoke-Worker $worker 'after-preview' 'Structure' 'GetVisualIndexes' $main @{}
    Assert-True ($afterPreview.result.indexes.name -notcontains $indexName) 'Dry-run persisted the index in the KB.'
    Assert-True ([string] $afterPreview.result.versionToken -eq $beforeToken) 'get_indexes token changed after dry-run.'

    $created = Invoke-Worker $worker 'create-index' 'Structure' 'CreateIndex' $main @{
        dryRun = $false; baseVersion = $beforeToken; rollbackOnFailure = $true
    } $indexPayload
    Assert-True ([string] $created.code -eq 'IndexCreated') 'Effective call did not return IndexCreated.'
    Assert-True ([bool] $created.result.saved) 'Effective call did not report saved=true.'
    Assert-True ([bool] $created.result.persisted) 'Effective call did not report persisted=true.'
    Assert-True ([bool] $created.result.verified) 'Effective call did not verify the persisted index.'
    Assert-True ($created.result.implicitOperations.Count -eq 0) 'Effective call reported implicit lifecycle operations.'
    $indexCreated = $true

    Stop-TestWorker $worker
    $worker = Start-TestWorker
    $persisted = Invoke-Worker $worker 'persisted' 'Structure' 'GetVisualIndexes' $main @{}
    $persistedIndex = @($persisted.result.indexes | Where-Object { $_.name -eq $indexName })
    Assert-True ($persistedIndex.Count -eq 1) 'get_indexes did not return exactly one created index.'
    Assert-True (-not [bool] $persistedIndex[0].isUnique) 'The non-unique index was persisted as unique.'
    Assert-True ([string] $persistedIndex[0].source -eq 'User') 'The index source is not User.'
    Assert-True ((Compare-Object $attributes @($persistedIndex[0].attributes.name) -SyncWindow 0).Count -eq 0) `
        'The persisted member names/order differ from the request.'

    $stalePayload = @{
        name = $secondIndexName; unique = $false; attributes = @($attributes[0]); order = 'Ascending'
    } | ConvertTo-Json -Compress -Depth 10
    $conflict = Invoke-Worker $worker 'stale' 'Structure' 'CreateIndex' $main @{
        dryRun = $false; baseVersion = $beforeToken; rollbackOnFailure = $true
    } $stalePayload -AllowError
    Assert-True ([string] $conflict.error.code -eq 'VersionConflict') 'A stale writer did not receive VersionConflict.'
    $afterConflict = Invoke-Worker $worker 'after-conflict' 'Structure' 'GetVisualIndexes' $main @{}
    Assert-True ($afterConflict.result.indexes.name -notcontains $secondIndexName) 'The stale writer persisted an index.'

    [pscustomobject]@{
        Passed = $true
        DryRunPersisted = $preview.result.persisted
        VersionUnchanged = $preview.result.versionUnchanged
        PersistedIndex = $indexName
        PersistedAttributes = @($persistedIndex[0].attributes.name)
        StaleWriteCode = $conflict.error.code
        ImplicitOperations = @($created.result.implicitOperations)
    }
}
finally {
    if ($null -ne $worker) {
        if ($indexCreated) {
            try {
                $dropPayload = @{ indexName = $indexName } | ConvertTo-Json -Compress
                Invoke-Worker $worker 'cleanup-index' 'Structure' 'DropIndex' $main @{} $dropPayload -AllowError | Out-Null
            } catch { }
        }
        foreach ($transaction in $transactions) {
            try {
                Invoke-Worker $worker ('cleanup-' + $transaction) 'Object' 'Delete' $transaction @{
                    type = 'Transaction'; confirm = $true
                } -AllowError | Out-Null
            } catch { }
        }
        Stop-TestWorker $worker
    }
}
