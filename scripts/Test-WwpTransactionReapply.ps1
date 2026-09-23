#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerExe,
    [Parameter(Mandatory)] [string] $GeneXusPath,
    [Parameter(Mandatory)] [string] $KbPath,
    [Parameter(Mandatory)] [switch] $ConfirmDisposableKb,
    [Parameter(Mandatory)] [string] $EvidenceFile
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDisposableKb) { throw 'ConfirmDisposableKb must be true.' }
$resolvedKb = (Resolve-Path -LiteralPath $KbPath).Path
if ($resolvedKb -notmatch '(?i)[\\/]\.test-kbs[\\/]' -or
    -not (Test-Path -LiteralPath (Join-Path $resolvedKb 'knowledgebase.connection'))) {
    throw 'An existing disposable KB inside .test-kbs is required.'
}
if (Test-Path -LiteralPath $EvidenceFile) { throw 'EvidenceFile already exists; choose a fresh path.' }
$null = New-Item -ItemType File -Path $EvidenceFile
$owned = [Collections.Generic.List[object]]::new()
$requestNumber = 0
$worker = $null
$workerStateUnknown = $false
$prefix = 'WwpTrnCheck' + [Guid]::NewGuid().ToString('N').Substring(0, 8)

# Reuse the existing transport and safety helpers without executing its layout scenario.
$parseErrors = $null
$tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Test-WwpLayoutAuthoring.ps1'), [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count) { throw 'The shared layout harness failed PowerShell parsing.' }
foreach ($functionName in @('Assert-True', 'Read-WorkerLine', 'Start-TestWorker', 'Stop-TestWorker',
    'Invoke-Worker', 'New-OwnedObject', 'Read-Part', 'Get-ErrorCode', 'Read-XmlPreservingWhitespace')) {
    $definition = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) |
        Where-Object Name -eq $functionName
    if (@($definition).Count -ne 1) { throw "Missing shared helper: $functionName" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Get-PatternState([string] $HostName) {
    $part = Read-Part $HostName 'PatternInstance'
    Assert-True (-not [string]::IsNullOrWhiteSpace($part.versionToken)) 'Missing pattern version token.'
    return @{ Xml = [string] $part.source; Token = [string] $part.versionToken }
}
function Assert-PatternUnchanged($Before, [string] $Label) {
    $after = Get-PatternState $hostName
    Assert-True ($Before.Xml -ceq $after.Xml -and $Before.Token -ceq $after.Token) "$Label changed pattern content or version."
}
function Set-ExportAction([string] $Value) {
    $state = Get-PatternState $hostName
    $xml = Read-XmlPreservingWhitespace $state.Xml
    $nodes = $xml.SelectNodes("//*[local-name()='selection']//*[@name='Export']")
    Assert-True ($nodes.Count -eq 1) 'Expected exactly one Export action in the selection; no structural edit will be guessed.'
    $nodes[0].SetAttribute('includeAction', $Value)
    $write = Invoke-Worker 'Write' 'PatternInstance' $hostName @{
        content = $xml.OuterXml; expectedVersion = $state.Token; validate = $false
    } -AllowError
    if ($write.status -ne 'ok') {
        $script:workerStateUnknown = $true
        throw "Pattern property write failed; retain evidence and objects: $($write | ConvertTo-Json -Compress -Depth 30)"
    }
    $saved = Read-XmlPreservingWhitespace (Get-PatternState $hostName).Xml
    Assert-True ($saved.SelectSingleNode("//*[local-name()='selection']//*[@name='Export']").GetAttribute('includeAction') -ceq $Value) 'Export includeAction did not persist.'
}
function Reapply-Transaction {
    $result = Invoke-Worker 'Pattern' 'Apply' $parent @{
        pattern = 'WorkWithPlus'; reapply = $true; validate = $false
    } -AllowError
    $receipt = if ($result.result) { $result.result } else { $result }
    if ($result.status -notin @('ok', 'partial') -or $receipt.nativeApplySucceeded -ne $true) {
        $script:workerStateUnknown = $true
        throw "Native full reapply did not confirm completion: $($result | ConvertTo-Json -Compress -Depth 30)"
    }
    Assert-True ($receipt.projectionScope -eq 'parent-and-generated-children') 'Reapply only projected the parent.'
    return $receipt
}
function Read-Child([string] $Name) {
    $identity = (Invoke-Worker 'Object' 'Read' $Name @{}).result.identity
    Assert-True (-not [string]::IsNullOrWhiteSpace($identity.guid)) "Missing child identity: $Name"
    return @{ Name = $Name; Guid = [string] $identity.guid; Events = [string] (Read-Part $Name 'Events').source }
}

try {
    $worker = Start-TestWorker
    $parent = $prefix
    $hostName = 'WorkWithPlus' + $parent
    $null = New-OwnedObject $parent 'Transaction' @{ firstItem = $prefix + 'Id'; firstItemType = 'Numeric(8.0)' }
    $structure = Read-Part $parent 'Structure'
    $setup = Invoke-Worker 'Write' 'Structure' $parent @{
        type = 'Transaction'
        content = "$($prefix)Id* : Numeric(8.0)`r`n$($prefix)Name : Character(40)"
        expectedVersion = $structure.versionToken; validate = $false
    } -AllowError
    $normalizedStructure = $setup.code -eq 'WriteNotPersisted' -and
        $setup.sdkSaveCompleted -eq $true -and $setup.result.persistedVerified -eq $true
    if ($setup.status -ne 'ok' -and -not $normalizedStructure) { $workerStateUnknown = $true; throw 'Synthetic Transaction structure setup failed; no cleanup.' }
    $createdStructure = Read-Part $parent 'Structure'
    Assert-True ($createdStructure.source -match [regex]::Escape($prefix + 'Id') -and
        $createdStructure.source -match [regex]::Escape($prefix + 'Name')) 'Synthetic ID/Name structure not confirmed.'
    $idLine = '(?im)^' + [regex]::Escape($prefix + 'Id') + '\*\s*:\s*NUMERIC\(8(?:\.0)?\)(?:\s*//[^\r\n]*)?\s*$'
    $nameLine = '(?im)^' + [regex]::Escape($prefix + 'Name') + '\s*:\s*CHARACTER\(40\)(?:\s*//[^\r\n]*)?\s*$'
    Assert-True ($createdStructure.source -match $idLine -and $createdStructure.source -match $nameLine -and
        @($createdStructure.source -split '\r?\n' | Where-Object { $_.Trim() }).Count -eq 2) 'Native structure differs from the two requested attributes and types.'
    $apply = Invoke-Worker 'Pattern' 'Apply' $parent @{ pattern = 'WorkWithPlus'; validate = $false } -AllowError
    if ($apply.status -ne 'ok') { $workerStateUnknown = $true; throw "Initial apply failed: $($apply | ConvertTo-Json -Compress -Depth 30)" }
    $hostRead = Invoke-Worker 'Object' 'Read' $hostName @{ type = 'WorkWithPlus' }
    Assert-True (-not [string]::IsNullOrWhiteSpace($hostRead.result.identity.guid)) 'Host identity missing.'

    # These names are not supported by the installed WorkWithPlus attribute specification.
    # Mix each invalid property with a legitimate change to expose partial-save regressions.
    foreach ($unsupported in @('includeInExportImportExcel', 'includeInExportImportCsv')) {
        $before = Get-PatternState $hostName
        $xml = Read-XmlPreservingWhitespace $before.Xml
        $attributeIdentity = (Invoke-Worker 'Object' 'Read' ($prefix + 'Name') @{ type = 'Attribute' }).result.identity
        Assert-True (-not [string]::IsNullOrWhiteSpace($attributeIdentity.entityTypeGuid)) 'Native Attribute type identity missing.'
        $attributeReference = [string] $attributeIdentity.entityTypeGuid + '-' + $prefix + 'Name'
        $attribute = $xml.SelectSingleNode("//*[local-name()='selection']//*[local-name()='attribute' and @attribute='$attributeReference']")
        Assert-True ($null -ne $attribute) 'Synthetic Name attribute not present in pattern.'
        $attribute.SetAttribute('description', 'Synthetic export validation')
        $attribute.SetAttribute($unsupported, 'False')
        $rejected = Invoke-Worker 'Write' 'PatternInstance' $hostName @{
            content = $xml.OuterXml; expectedVersion = $before.Token; validate = $false
        } -AllowError
        Assert-True ((Get-ErrorCode $rejected) -eq 'PatternPropertyUnsupported') "Unsupported property was not rejected: $unsupported"
        Assert-PatternUnchanged $before $unsupported
    }

    Set-ExportAction 'Yes'
    $baselineApply = Reapply-Transaction
    # WorkWithPlus can bind the action directly to an Event or dispatch a dropdown
    # option to a generated Sub. Both forms must disappear when Export is disabled.
    $eventPattern = '(?im)^\s*(?:Event|Sub)\s+[''"](?:Do)?Export(?:Excel)?[''"]\s*$'
    $baseline = @()
    foreach ($name in @($baselineApply.generatedObjects)) {
        if ([string] $name -notlike "*$prefix*" -or [string] $name -eq $hostName -or [string] $name -eq $parent) { continue }
        $events = Invoke-Worker 'Read' 'ExtractSource' ([string] $name) @{ part = 'Events'; limit = 1000000 } -AllowError
        if ($events.status -eq 'error' -or $events.error) { continue }
        $data = if ($null -ne $events.result.source) { $events.result } else { $events }
        if ($data.source -match $eventPattern) { $baseline += Read-Child ([string] $name) }
    }
    Assert-True ($baseline.Count -gt 0) 'No independently read generated child contains an Export event; integration not proved.'
    Set-ExportAction 'No'
    $null = Reapply-Transaction
    foreach ($child in $baseline) {
        $after = Read-Child $child.Name
        Assert-True ($after.Guid -ceq $child.Guid) 'Generated child identity changed unexpectedly.'
        Assert-True ($after.Events -cne $child.Events) 'Generated child Events stayed stale after native reapply.'
        Assert-True ($after.Events -notmatch $eventPattern) 'Disabled Export event remains in generated child.'
    }
    Stop-TestWorker $worker
    $worker = $null
    $worker = Start-TestWorker
    foreach ($child in $baseline) {
        $durable = Read-Child $child.Name
        Assert-True ($durable.Guid -ceq $child.Guid -and $durable.Events -notmatch $eventPattern) 'Reopened worker did not confirm child persistence.'
    }
    [pscustomobject] @{ Passed = $true; Parent = $parent; Host = $hostName; Children = @($baseline.Name)
        UnsupportedPropertiesRejected = $true; ChildEventsChanged = $true; WorkerReopens = 1
        ImplicitLifecycleActions = @(); Cleanup = 'Objects retained in disposable KB for evidence; no automatic deletion.' }
}
finally {
    # Native apply can create shared resources. Retain all artifacts; never infer
    # ownership from name conventions or delete anything after an uncertain write.
    if ($EvidenceFile) {
        @{ retainedParent = $prefix; workerStateUnknown = $workerStateUnknown; workerPid = $worker.Id; owned = @($owned) } |
            ConvertTo-Json -Compress -Depth 15 | Add-Content -LiteralPath $EvidenceFile -Encoding utf8
    }
    Stop-TestWorker $worker
}
