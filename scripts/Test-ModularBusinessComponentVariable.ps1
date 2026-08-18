[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerExe,
    [Parameter(Mandatory)] [string] $GeneXusPath,
    [Parameter(Mandatory)] [string] $KbPath,
    [Parameter(Mandatory)] [switch] $ConfirmDisposableKb
)

$ErrorActionPreference = 'Stop'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

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

function Invoke-Worker($Process, [string] $Id, [string] $Method, [string] $Action,
    [string] $Target, [hashtable] $Arguments, [string] $Payload = $null, [switch] $AllowError) {
    $request = [ordered]@{
        jsonrpc = '2.0'; id = $Id; method = $Method; action = $Action
        target = $Target; params = $Arguments
    }
    if ($Arguments.ContainsKey('dryRun')) { $request.dryRun = [bool] $Arguments.dryRun }
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

$resolvedKb = (Resolve-Path -LiteralPath $KbPath).Path
if ($resolvedKb -notmatch '(?i)[\\/]\.test-kbs[\\/]') {
    throw "Refusing to mutate a KB outside a .test-kbs directory: $resolvedKb"
}
if (-not (Test-Path -LiteralPath (Join-Path $resolvedKb 'knowledgebase.connection'))) {
    throw "Not a GeneXus KB: $resolvedKb"
}

$suffix = [DateTime]::UtcNow.ToString('MMddHHmmss')
$moduleName = "SyntheticOps$suffix"
$transaction = "SyntheticRecord$suffix"
$procedure = "SyntheticUpdater$suffix"
$variable = 'Record'
$worker = $null

try {
    $worker = Start-TestWorker
    Invoke-Worker $worker 'module' 'Object' 'Create' $moduleName @{ type = 'Module' } | Out-Null
    Invoke-Worker $worker 'transaction' 'Object' 'Create' $transaction @{
        type = 'Transaction'; destModule = $moduleName
    } | Out-Null
    Invoke-Worker $worker 'bc' 'Property' 'Set' $transaction @{
        type = 'Transaction'; propertyName = 'idISBUSINESSCOMPONENT'; value = 'True'
    } | Out-Null
    Invoke-Worker $worker 'procedure' 'Object' 'Create' $procedure @{ type = 'Procedure' } | Out-Null

    $preview = Invoke-Worker $worker 'variable-preview' 'Write' 'AddVariable' $procedure @{
        varName = $variable; objectType = 'BusinessComponent'; objectName = $transaction
        objectModule = $moduleName; dryRun = $true; rollbackOnFailure = $true
    }
    Assert-True ([string] $preview.code -eq 'DryRun') 'Variable dry-run did not return DryRun.'
    Assert-True (-not [bool] $preview.result.persisted) 'Variable dry-run reported persisted=true.'
    Assert-True (-not [bool] $preview.result.mutationDetected) 'Variable dry-run reported a mutation.'
    Assert-True ([string] $preview.result.beforeVersion -eq [string] $preview.result.afterVersion) 'Variable dry-run changed the version.'
    Assert-True ([string] $preview.result.typedIdentity.module -eq $moduleName) 'Variable dry-run resolved the wrong module.'
    Assert-True ([string]::IsNullOrWhiteSpace([string] $preview.result.typedIdentity.guid) -eq $false) 'Variable dry-run did not resolve a native GUID.'

    $beforeVariables = Invoke-Worker $worker 'before-variables' 'Read' 'GetVariables' $procedure @{}
    Assert-True (($beforeVariables | ConvertTo-Json -Depth 30) -notmatch ('"name":"?' + [regex]::Escape($variable) + '"?')) 'Variable dry-run persisted the variable.'

    $added = Invoke-Worker $worker 'variable-add' 'Write' 'AddVariable' $procedure @{
        varName = $variable; objectType = 'BusinessComponent'; objectName = $transaction
        objectModule = $moduleName; expectedVersion = [string] $preview.result.versionToken
        rollbackOnFailure = $true
    }
    Assert-True ([bool] $added.result.persisted) 'Native Business Component variable was not persisted.'
    Assert-True ([bool] $added.result.reReadConfirmed) 'Native Business Component variable reread was not confirmed.'
    Assert-True ([string] $added.result.typedIdentity.guid -eq [string] $preview.result.typedIdentity.guid) 'Persisted variable references a different GUID.'
    Assert-True ([string] $added.result.typedIdentity.module -eq $moduleName) 'Persisted variable lost its module.'
    foreach ($method in @('Load', 'Save', 'Success', 'GetMessages')) {
        Assert-True (@($added.result.typedIdentity.methods) -contains $method) "BC method '$method' was not reported."
    }

    $source = @"
&$variable.Load(&$($transaction)Id)
&$variable.Save()
if &$variable.Success()
    &Messages = &$variable.GetMessages()
endif
"@
    $sourceWrite = Invoke-Worker $worker 'source' 'Write' 'Source' $procedure @{
        type = 'Procedure'; part = 'Source'
    } $source
    $sourceJson = $sourceWrite | ConvertTo-Json -Compress -Depth 30
    Assert-True ($sourceJson -notmatch 'src0294') 'Saving BC method calls returned src0294.'

    $visual = Invoke-Worker $worker 'structure-read' 'Structure' 'GetVisualStructure' $transaction @{}
    $structurePayload = @{ children = @($visual.result.children) } | ConvertTo-Json -Compress -Depth 30
    $structurePreview = Invoke-Worker $worker 'structure-preview' 'Structure' 'UpdateVisualStructure' $transaction @{
        transactionModule = $moduleName; dryRun = $true; rollbackOnFailure = $true
    } $structurePayload
    Assert-True (-not [bool] $structurePreview.result.persisted) 'Structure dry-run reported persisted=true.'
    Assert-True (-not [bool] $structurePreview.result.mutationDetected) 'Structure dry-run reported a mutation.'
    Assert-True ([string] $structurePreview.result.beforeVersion -eq [string] $structurePreview.result.afterVersion) 'Structure dry-run changed the version.'

    $structureWrite = Invoke-Worker $worker 'structure-update' 'Structure' 'UpdateVisualStructure' $transaction @{
        transactionModule = $moduleName; expectedVersion = [string] $structurePreview.result.versionToken
        rollbackOnFailure = $true
    } $structurePayload
    Assert-True ([string] $structureWrite.code -eq 'StructureUpdated') 'Structure update returned a false failure.'
    Assert-True ([bool] $structureWrite.result.persisted) 'Structure update was not reread as persisted.'
    Assert-True ([bool] $structureWrite.result.persistedVerified) 'Structure reread was not confirmed.'

    $failureChildren = $visual.result.children | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $failureChildren[0].description = 'Must be rolled back'
    $failureChildren[0].type = 'NUMERIC(999999999999999999999999)'
    $failurePayload = @{ children = @($failureChildren) } | ConvertTo-Json -Compress -Depth 30
    $failedStructure = Invoke-Worker $worker 'structure-failure' 'Structure' 'UpdateVisualStructure' $transaction @{
        transactionModule = $moduleName; expectedVersion = [string] $structureWrite.result.versionToken
        rollbackOnFailure = $true
    } $failurePayload -AllowError
    Assert-True ([string] $failedStructure.status -eq 'error') 'Invalid Transaction structure unexpectedly succeeded.'
    Assert-True (-not [bool] $failedStructure.persisted) 'Failed structure update reported persisted=true.'
    Assert-True ([bool] $failedStructure.rollback.verified) 'Failed structure update did not verify full rollback.'
    $afterFailure = Invoke-Worker $worker 'structure-after-failure' 'Structure' 'GetVisualStructure' $transaction @{}
    Assert-True (($afterFailure.result.children | ConvertTo-Json -Compress -Depth 30) -eq
        ($visual.result.children | ConvertTo-Json -Compress -Depth 30)) 'Failed structure update left a partial Structure.'

    $stale = Invoke-Worker $worker 'structure-stale' 'Structure' 'UpdateVisualStructure' $transaction @{
        transactionModule = $moduleName; expectedVersion = 'stale-version'; rollbackOnFailure = $true
    } $structurePayload -AllowError
    Assert-True ([string] $stale.error.code -eq 'VersionConflict') 'Stale structure update was not rejected.'
    Assert-True (-not [bool] $stale.error.persisted) 'Stale structure update reported persistence.'

    Stop-TestWorker $worker
    $worker = Start-TestWorker
    $reopened = Invoke-Worker $worker 'reopen-variable' 'Write' 'AddVariable' $procedure @{
        varName = $variable; objectType = 'BusinessComponent'; objectName = $transaction
        objectModule = $moduleName; dryRun = $false; rollbackOnFailure = $true
    }
    Assert-True ([string] $reopened.code -eq 'WriteNoChange') 'Fresh reread did not recognize the same native BC binding.'
    Assert-True ([string] $reopened.result.typedIdentity.guid -eq [string] $added.result.typedIdentity.guid) 'Fresh reread resolved a different BC GUID.'
    $existingPreview = Invoke-Worker $worker 'reopen-dry-run' 'Write' 'AddVariable' $procedure @{
        varName = $variable; objectType = 'BusinessComponent'; objectName = $transaction
        objectModule = $moduleName; dryRun = $true; rollbackOnFailure = $true
    }
    Assert-True ([string] $existingPreview.code -eq 'DryRun') 'Existing-variable dry-run did not return DryRun.'
    Assert-True (-not [bool] $existingPreview.result.persisted) 'Existing-variable dry-run reported persisted=true.'

    [pscustomobject]@{
        Passed = $true
        DryRunPersisted = $preview.result.persisted
        DryRunMutationDetected = $preview.result.mutationDetected
        VersionStable = ([string] $preview.result.beforeVersion -eq [string] $preview.result.afterVersion)
        NativeGuidConfirmed = ([string] $reopened.result.typedIdentity.guid -eq [string] $added.result.typedIdentity.guid)
        ModuleConfirmed = ([string] $reopened.result.typedIdentity.module -eq $moduleName)
        Methods = @($added.result.typedIdentity.methods)
        SourceSrc0294 = ($sourceJson -match 'src0294')
        StructurePersisted = $structureWrite.result.persisted
        StructureRereadConfirmed = $structureWrite.result.persistedVerified
        StructureFailurePersisted = $failedStructure.persisted
        StructureRollbackVerified = $failedStructure.rollback.verified
        StaleWriteCode = $stale.error.code
        ImplicitLifecycleActions = @($added.result.implicitLifecycleActions)
    }
}
finally {
    if ($null -ne $worker) {
        try { Invoke-Worker $worker 'cleanup-procedure' 'Object' 'Delete' $procedure @{ type = 'Procedure'; confirm = $true } -AllowError | Out-Null } catch { }
        try { Invoke-Worker $worker 'cleanup-transaction' 'Object' 'Delete' $transaction @{ type = 'Transaction'; confirm = $true } -AllowError | Out-Null } catch { }
        try { Invoke-Worker $worker 'cleanup-attribute' 'Object' 'Delete' ($transaction + 'Id') @{ type = 'Attribute'; confirm = $true } -AllowError | Out-Null } catch { }
        try { Invoke-Worker $worker 'cleanup-module' 'Object' 'Delete' $moduleName @{ type = 'Module'; confirm = $true } -AllowError | Out-Null } catch { }
    }
    Stop-TestWorker $worker
}
