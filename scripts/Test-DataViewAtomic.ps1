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
    [string] $Target, [hashtable] $Arguments, [switch] $AllowError) {
    $request = [ordered]@{
        jsonrpc = '2.0'; id = $Id; method = $Module; action = $Action
        target = $Target; params = $Arguments
    }
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

$suffix = [DateTime]::UtcNow.ToString('MMddHHmmss')
$sourceTransaction = "DvBase$suffix"
$sourceAttribute = $sourceTransaction + 'Id'
$dryOnlyTransaction = "DvDryOnly$suffix"
$parallelTransaction = "DvParallel$suffix"
$dataViewName = "DvMap$suffix"
$worker = $null
$sourceCreated = $false
$pairCreated = $false

$definition = @{
    action = 'dry_run'
    transaction = $parallelTransaction
    dataViewName = $dataViewName
    dataStore = 'Default'
    schema = 'APP'
    table = $sourceTransaction
    updatable = $true
    rollbackOnFailure = $true
    attributeMappings = @(
        @{ attribute = $sourceAttribute; column = $sourceAttribute; key = $true }
    )
}

try {
    $worker = Start-TestWorker
    $objectPreview = Invoke-Worker $worker 'object-preview' 'Object' 'Create' $dryOnlyTransaction @{
        type = 'Transaction'; dryRun = $true
    }
    Assert-True ([string] $objectPreview.code -eq 'DryRun') 'Transaction object dry-run did not return DryRun.'
    Assert-True (-not [bool] $objectPreview.result.persisted) 'Transaction object dry-run reported persisted=true.'
    Assert-True (-not [bool] $objectPreview.result.mutationDetected) 'Transaction object dry-run reported a mutation.'

    Stop-TestWorker $worker
    $worker = Start-TestWorker
    $missingDryTransaction = Invoke-Worker $worker 'object-preview-trn' 'Object' 'Read' $dryOnlyTransaction @{ type = 'Transaction' } -AllowError
    $missingDrySeed = Invoke-Worker $worker 'object-preview-attr' 'Object' 'Read' ($dryOnlyTransaction + 'Id') @{ type = 'Attribute' } -AllowError
    Assert-True ([string] $missingDryTransaction.status -eq 'error') 'Transaction object dry-run persisted the Transaction.'
    Assert-True ([string] $missingDrySeed.status -eq 'error') 'Transaction object dry-run persisted the seed Attribute.'

    Invoke-Worker $worker 'source-create' 'Object' 'Create' $sourceTransaction @{ type = 'Transaction' } | Out-Null
    $sourceCreated = $true

    $preview = Invoke-Worker $worker 'preview' 'DataView' 'Run' $parallelTransaction $definition
    Assert-True ([string] $preview.code -eq 'DataViewDryRun') 'dry_run did not return DataViewDryRun.'
    Assert-True (-not [bool] $preview.result.persisted) 'dry_run reported persisted=true.'
    Assert-True (-not [bool] $preview.result.mutationDetected) 'dry_run reported a mutation.'
    Assert-True ([string] $preview.result.physicalTable -eq "APP.$sourceTransaction") 'Physical mapping preview is wrong.'
    Assert-True ($preview.result.newTables.Count -eq 0) 'dry_run proposed a new physical table.'
    Assert-True (-not [bool] $preview.result.reorgRequired) 'dry_run reported reorgRequired=true.'
    Assert-True ($preview.result.implicitLifecycleActions.Count -eq 0) 'dry_run reported implicit lifecycle actions.'
    $baselineVersion = [string] $preview.result.version

    Stop-TestWorker $worker
    $worker = Start-TestWorker
    $missingTransaction = Invoke-Worker $worker 'after-preview-trn' 'Object' 'Read' $parallelTransaction @{ type = 'Transaction' } -AllowError
    $missingSeed = Invoke-Worker $worker 'after-preview-attr' 'Object' 'Read' ($parallelTransaction + 'Id') @{ type = 'Attribute' } -AllowError
    Assert-True ([string] $missingTransaction.status -eq 'error') 'dry_run persisted the target Transaction.'
    Assert-True ([string] $missingSeed.status -eq 'error') 'dry_run created a seed global Attribute.'

    $createArgs = @{} + $definition
    $createArgs.action = 'create'
    $createArgs.expectedVersion = $baselineVersion
    $created = Invoke-Worker $worker 'create-pair' 'DataView' 'Run' $parallelTransaction $createArgs
    Assert-True ([string] $created.code -eq 'DataViewCreated') 'create did not return DataViewCreated.'
    Assert-True ([bool] $created.result.persisted) 'create did not report persisted=true.'
    Assert-True ([bool] $created.result.reread.confirmed) 'create reread did not confirm the persisted pair.'
    Assert-True ([bool] $created.result.businessComponent) 'Business Component was not enabled.'
    Assert-True ([bool] $created.result.rootOnly) 'The Transaction is not root-only.'
    Assert-True ($created.result.newTables.Count -eq 0) 'create proposed a physical table.'
    Assert-True (-not [bool] $created.result.reorgRequired) 'create reported reorgRequired=true.'
    Assert-True ($created.result.implicitLifecycleActions.Count -eq 0) 'create reported implicit lifecycle actions.'
    $pairCreated = $true

    $staleArgs = @{} + $definition
    $staleArgs.action = 'update'
    $staleArgs.expectedVersion = $baselineVersion
    $stale = Invoke-Worker $worker 'stale-update' 'DataView' 'Run' $parallelTransaction $staleArgs -AllowError
    Assert-True ([string] $stale.error.code -eq 'ConcurrentModification') 'A stale update was not rejected.'

    Stop-TestWorker $worker
    $worker = Start-TestWorker
    $inspected = Invoke-Worker $worker 'inspect' 'DataView' 'Run' $parallelTransaction @{
        action = 'inspect'; transaction = $parallelTransaction; dataViewName = $dataViewName
    }
    Assert-True ([bool] $inspected.result.businessComponent) 'Persisted reread lost the Business Component property.'
    Assert-True ([bool] $inspected.result.rootOnly) 'Persisted reread found a nested level.'
    Assert-True ([bool] $inspected.result.associatedTableVerified) 'Persisted reread did not verify the associated logical table.'
    Assert-True ([string] $inspected.result.physicalTable -eq "APP.$sourceTransaction") 'Persisted physical mapping is wrong.'
    Assert-True ($inspected.result.rootAttributes.Count -eq 1) 'The Business Component contains more than the requested root attribute.'

    $deletePreview = Invoke-Worker $worker 'delete-preview' 'DataView' 'Run' $parallelTransaction @{
        action = 'delete'; transaction = $parallelTransaction; dataViewName = $dataViewName
        expectedVersion = [string] $inspected.result.version; dryRun = $true; rollbackOnFailure = $true
    }
    Assert-True ([string] $deletePreview.code -eq 'DataViewDeleteDryRun') 'delete dry-run did not return DataViewDeleteDryRun.'
    Assert-True (-not [bool] $deletePreview.result.persisted) 'delete dry-run reported a persisted mutation.'
    Assert-True (-not [bool] $deletePreview.result.mutationDetected) 'delete dry-run reported a mutation.'
    $afterDeletePreview = Invoke-Worker $worker 'inspect-after-delete-preview' 'DataView' 'Run' $parallelTransaction @{
        action = 'inspect'; transaction = $parallelTransaction; dataViewName = $dataViewName
    }
    Assert-True ([bool] $afterDeletePreview.result.transactionExists) 'delete dry-run removed the Transaction.'
    Assert-True ([bool] $afterDeletePreview.result.dataViewExists) 'delete dry-run removed the Data View.'

    $deleted = Invoke-Worker $worker 'delete-pair' 'DataView' 'Run' $parallelTransaction @{
        action = 'delete'; transaction = $parallelTransaction; dataViewName = $dataViewName
        expectedVersion = [string] $afterDeletePreview.result.version; rollbackOnFailure = $true
    }
    Assert-True ([string] $deleted.code -eq 'DataViewDeleted') 'delete did not return DataViewDeleted.'
    Assert-True ([bool] $deleted.result.transactionRemoved) 'delete left the Transaction.'
    Assert-True ([bool] $deleted.result.dataViewRemoved) 'delete left the Data View.'
    Assert-True ($deleted.result.globalAttributesRemoved.Count -eq 0) 'delete removed a global Attribute.'
    $pairCreated = $false

    [pscustomobject]@{
        Passed = $true
        DryRunPersisted = $preview.result.persisted
        DryRunMutationDetected = $preview.result.mutationDetected
        ObjectDryRunMutationDetected = $objectPreview.result.mutationDetected
        SeedAttributeCreated = $false
        PhysicalTable = $created.result.physicalTable
        NewTables = @($created.result.newTables)
        ReorgRequired = $created.result.reorgRequired
        BusinessComponent = $inspected.result.businessComponent
        RootAttributeCount = $inspected.result.rootAttributes.Count
        StaleWriteCode = $stale.error.code
        DeleteDryRunMutationDetected = $deletePreview.result.mutationDetected
        RereadConfirmed = $created.result.reread.confirmed
        ImplicitLifecycleActions = @($created.result.implicitLifecycleActions)
    }
}
finally {
    if ($null -ne $worker) {
        if ($pairCreated) {
            try {
                $current = Invoke-Worker $worker 'cleanup-inspect' 'DataView' 'Run' $parallelTransaction @{
                    action = 'inspect'; transaction = $parallelTransaction; dataViewName = $dataViewName
                } -AllowError
                Invoke-Worker $worker 'cleanup-pair' 'DataView' 'Run' $parallelTransaction @{
                    action = 'delete'; transaction = $parallelTransaction; dataViewName = $dataViewName
                    expectedVersion = [string] $current.result.version; rollbackOnFailure = $true
                } -AllowError | Out-Null
            } catch { }
        }
        if ($sourceCreated) {
            try { Invoke-Worker $worker 'cleanup-source' 'Object' 'Delete' $sourceTransaction @{ type = 'Transaction'; confirm = $true } -AllowError | Out-Null } catch { }
            try { Invoke-Worker $worker 'cleanup-attribute' 'Object' 'Delete' $sourceAttribute @{ type = 'Attribute'; confirm = $true } -AllowError | Out-Null } catch { }
        }
        Stop-TestWorker $worker
    }
}
