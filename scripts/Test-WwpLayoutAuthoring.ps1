#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerExe,
    [Parameter(Mandatory)] [string] $GeneXusPath,
    [Parameter(Mandatory)] [string] $KbPath,
    [Parameter(Mandatory)] [switch] $ConfirmDisposableKb,
    [string] $EvidenceFile
)

$ErrorActionPreference = 'Stop'
if (-not $ConfirmDisposableKb) { throw 'ConfirmDisposableKb must be true.' }
$resolvedKb = (Resolve-Path -LiteralPath $KbPath).Path
if ($resolvedKb -notmatch '(?i)[\\/]\.test-kbs[\\/]' -or
    -not (Test-Path -LiteralPath (Join-Path $resolvedKb 'knowledgebase.connection'))) {
    throw 'An existing disposable KB inside .test-kbs is required.'
}
$owned = [Collections.Generic.List[object]]::new()
$requestNumber = 0
$worker = $null
$workerStateUnknown = $false
$prefix = 'WwpLayoutCheck' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
if ($EvidenceFile) {
    if (Test-Path -LiteralPath $EvidenceFile) { throw 'EvidenceFile already exists; choose a fresh path.' }
    $null = New-Item -ItemType File -Path $EvidenceFile
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Read-XmlPreservingWhitespace([string] $Source) {
    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.XmlResolver = $null
    $document.LoadXml($Source)
    return ,$document
}

function Read-WorkerLine($Process) {
    $pending = $Process.StandardOutput.ReadLineAsync()
    if (-not $pending.Wait(120000)) { $script:workerStateUnknown = $true; throw 'Worker response timed out; state is unknown.' }
    if ($null -eq $pending.Result) { $script:workerStateUnknown = $true; throw 'Worker exited before returning a response.' }
    return $pending.Result
}

function Start-TestWorker {
    $script:workerStateUnknown = $false
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Resolve-Path -LiteralPath $WorkerExe).Path
    $start.ArgumentList.Add('--kb')
    $start.ArgumentList.Add($resolvedKb)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['GX_PROGRAM_DIR'] = (Resolve-Path -LiteralPath $GeneXusPath).Path
    $start.Environment['GX_KB_PATH'] = $resolvedKb
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    [void] $process.Start()
    # Drain stderr concurrently so SDK diagnostics cannot block the RPC stream.
    $process | Add-Member -NotePropertyName StderrDrain -NotePropertyValue $process.StandardError.ReadToEndAsync()
    try {
        do { $line = Read-WorkerLine $process } until ($line -match 'notifications/worker/sdk_ready')
        return $process
    }
    catch { Stop-TestWorker $process; throw }
}

function Stop-TestWorker($Process) {
    if ($null -eq $Process) { return }
    if ($script:workerStateUnknown -and -not $Process.HasExited) { Write-Warning "Worker PID $($Process.Id) retained because mutation state is unknown."; return }
    try { $Process.StandardInput.Close() } catch { }
    if (-not $Process.WaitForExit(15000)) { $Process.Kill(); $Process.WaitForExit() }
    $Process.Dispose()
}

function Invoke-Worker([string] $Method, [string] $Action, [string] $Target,
    [hashtable] $Arguments = @{}, [switch] $AllowError) {
    $script:requestNumber++
    $id = [string] $script:requestNumber
    $request = @{ jsonrpc = '2.0'; id = $id; method = $Method; action = $Action; target = $Target; params = $Arguments }
    if ($Arguments.ContainsKey('dryRun')) { $request.dryRun = $Arguments.dryRun }
    $worker.StandardInput.WriteLine(($request | ConvertTo-Json -Compress -Depth 30))
    $worker.StandardInput.Flush()
    while ($true) {
        $line = Read-WorkerLine $worker
        try { $response = $line | ConvertFrom-Json } catch { continue }
        if ([string] $response.id -ne $id) { continue }
        if ($EvidenceFile) {
            @{ request = $request; response = $response } | ConvertTo-Json -Compress -Depth 40 |
                Add-Content -LiteralPath $EvidenceFile -Encoding utf8
        }
        if ($response.error) { $script:workerStateUnknown = $true; throw "RPC $id failed: $($response.error.message)" }
        if ($response.result.status -eq 'Running' -or $response.result.operationId -or
            $response.result.result.persistedStateKnown -eq $false -or $response.result.persistedStateKnown -eq $false) {
            $script:workerStateUnknown = $true
            throw 'Nonterminal or unknown persistence response; do not retry or clean up blindly.'
        }
        # ExtractSource intentionally returns a direct source envelope, unlike canonical tools.
        $directSource = $Method -eq 'Read' -and $Action -eq 'ExtractSource' -and
            $null -eq $response.result.status -and $null -eq $response.result.error -and
            $response.result.source -is [string] -and $response.result.part -eq $Arguments.part
        if ($response.result.status -ne 'ok' -and -not $directSource -and -not $AllowError) {
            throw ($response.result | ConvertTo-Json -Compress -Depth 30)
        }
        return $response.result
    }
}

function New-OwnedObject([string] $Name, [string] $Type, [hashtable] $Options = @{}) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $before = Invoke-Worker 'Object' 'Read' $Name @{ type = $Type } -AllowError
        if ($before.error.code -ne 'ObjectNotFoundIndexWarming') { break }
        Start-Sleep -Milliseconds 2500
    }
    Assert-True ($before.status -eq 'error' -and $before.error.code -eq 'ObjectNotFound') "Object absence could not be verified: $Name; $($before | ConvertTo-Json -Compress -Depth 15)"
    $Options.type = $Type
    $created = Invoke-Worker 'Object' 'Create' $Name $Options
    $read = Invoke-Worker 'Object' 'Read' $Name @{ type = $Type }
    Assert-True (-not [string]::IsNullOrWhiteSpace($read.result.identity.guid)) "No identity for created $Name"
    $owned.Add(@{ Name = $Name; Type = $Type; Guid = [string] $read.result.identity.guid })
    return $read.result.identity
}

function Read-Part([string] $Target, [string] $Part) {
    $read = Invoke-Worker 'Read' 'ExtractSource' $Target @{ part = $Part; limit = 1000000 }
    $data = if ($null -ne $read.result.source) { $read.result } else { $read }
    Assert-True ($null -ne $data.source) "Complete $Part source not returned for $Target"
    Assert-True (-not $data.isBase64) 'Encoded source requires decoding before verification.'
    Assert-True (-not $data.truncated -and -not $data.hasMore) "Truncated $Part source cannot prove persistence."
    return $data
}

function Get-State([string] $Parent, [string] $HostName) {
    $pattern = Read-Part $HostName 'PatternInstance'
    Assert-True (-not [string]::IsNullOrWhiteSpace($pattern.versionToken)) 'PatternInstance token is missing.'
    $variableRead = Invoke-Worker 'Read' 'GetVariables' $Parent
    Assert-True ($null -ne $variableRead.result.variables -and $null -ne $variableRead.result.source) 'Native variable list and declarations are required.'
    $variableSnapshot = [ordered] @{
        variables = @($variableRead.result.variables | Sort-Object internalId, name)
        source = [string] $variableRead.result.source
    } | ConvertTo-Json -Compress -Depth 30
    return @{
        Pattern = [string] $pattern.source; Token = [string] $pattern.versionToken
        Form = [string] (Read-Part $Parent 'WebForm').source
        Events = [string] (Read-Part $Parent 'Events').source
        Variables = $variableSnapshot
    }
}

function Assert-SameState($Before, $After, [string] $Label) {
    foreach ($part in @('Pattern', 'Token', 'Form', 'Events', 'Variables')) {
        Assert-True ($Before[$part] -ceq $After[$part]) "$Label changed $part."
    }
}

function Get-ErrorCode($Response) {
    if ($Response.error.code) { return [string] $Response.error.code }
    return [string] $Response.code
}

function Read-PropertyValue([string] $Target, [string] $Control, [string[]] $Names, [switch] $Optional) {
    foreach ($property in $Names) {
        $propertyArgs = @{ propertyName = $property }
        if ($Control) { $propertyArgs.control = $Control }
        $read = Invoke-Worker 'Property' 'Get' $Target $propertyArgs -AllowError
        if ($read.status -eq 'ok' -and $null -ne $read.result.value) { return [string] $read.result.value }
    }
    if ($Optional) { return $null }
    throw "Required property unavailable: $Target $Control $($Names -join '/')"
}

function Get-VariableContract([string] $Parent, $DomainIdentity, [string] $DomainName) {
    $basedOn = Read-PropertyValue $Parent '&Application' @('DomainBasedOn', 'Domain')
    Assert-True ($basedOn -eq $DomainName) 'Native DomainBasedOn does not identify the synthetic domain.'
    $domainKey = Read-PropertyValue $Parent '&Application' @('DomainKey') -Optional
    Assert-True (-not [string]::IsNullOrWhiteSpace($domainKey)) 'NativeDomainIdentityUnavailable: Property service did not expose DomainKey; a display name is insufficient identity evidence.'
    Assert-True ($domainKey -eq [string] $DomainIdentity.entityKey) 'Native DomainKey differs from the created domain EntityKey.'
    $contract = @{
        DomainBasedOn = $basedOn; DomainKey = $domainKey
        ApplicationType = Read-PropertyValue $Parent '&Application' @('DataTypeString')
        ApplicationLength = Read-PropertyValue $Parent '&Application' @('Length', 'AttMaxLen')
        SimpleType = Read-PropertyValue $Parent '&Simple' @('DataTypeString')
        SimpleLength = Read-PropertyValue $Parent '&Simple' @('Length', 'AttMaxLen')
    }
    Assert-True ([int] $contract.ApplicationLength -eq 30) 'Domain variable length is not 30.'
    Assert-True ([int] $contract.SimpleLength -eq 50 -and $contract.SimpleType -match '(?i)VarChar') 'Simple variable is not VarChar(50).'
    return $contract
}

function Assert-VariableContract($Before, $After) {
    foreach ($key in $Before.Keys) { Assert-True ($Before[$key] -ceq $After[$key]) "Variable native type/domain contract changed: $key" }
}

function Assert-UserEventsPreserved([string] $Before, [string] $After) {
    # Only a newly generated, empty DoTestSave event may be added. All old code is exact.
    if ($Before -notmatch '(?im)^\s*Event\s+[''" ]DoTestSave[''" ]') {
        $markers = [regex]::Escape('/* Generated by DVelop Work With Plus Pattern [Start] - Do not change */') + '[ \t\r\n]*' + [regex]::Escape('/* Generated by DVelop Work With Plus Pattern [End] - Do not change */')
        $empty = '(?im)^[ \t]*Event[ \t]+[''"]DoTestSave[''"][ \t]*\r?\n[ \t\r\n]*(?:(?-i:' + $markers + ')[ \t\r\n]*)?EndEvent[ \t\r\n]*(?=\S|$)'
        $eventMatches = [regex]::Matches($After, $empty)
        Assert-True ($eventMatches.Count -le 1) 'Duplicate generated action event.'
        if ($eventMatches.Count -eq 1) { $After = $After.Remove($eventMatches[0].Index, $eventMatches[0].Length) }
    }
    Assert-True ($Before.TrimEnd("`r", "`n") -ceq $After.TrimEnd("`r", "`n")) 'Existing Events code changed.'
}

function Assert-LayoutPresent($State, $DomainIdentity, [string] $DomainName) {
    $pattern = Read-XmlPreservingWhitespace $State.Pattern
    Assert-True ($pattern.SelectNodes("//*[local-name()='variable' and @name='Application']").Count -eq 1) 'Domain variable not persisted exactly once.'
    Assert-True ($pattern.SelectNodes("//*[local-name()='variable' and @name='Simple']").Count -eq 1) 'Simple variable not persisted exactly once.'
    Assert-True ($pattern.SelectNodes("//*[local-name()='table' and @name='TestFields']").Count -eq 1) 'Nested table not persisted.'
    Assert-True ($pattern.SelectNodes("//*[local-name()='userAction' and @name='TestSave']").Count -eq 1) 'Button not persisted.'
    Assert-True ($State.Form -match 'TestFields' -and $State.Form -match 'TestSave') 'Projected table/button missing from WebForm.'
    Assert-True ($State.Form -match 'var:') 'No native variable binding in projected WebForm.'
    $domainVariable = $pattern.SelectSingleNode("//*[local-name()='variable' and @name='Application']")
    # Pattern XML exports a type GUID plus object name, not the object's instance GUID.
    $reference = [regex]::Match([string] $domainVariable.domain, '^(?<type>[0-9a-fA-F-]{36})-(?<name>.+)$')
    Assert-True ($reference.Success) 'Unexpected serialized pattern domain reference format.'
    Assert-True ([Guid] $reference.Groups['type'].Value -eq [Guid] $DomainIdentity.entityTypeGuid) 'Pattern reference identifies a non-Domain object type.'
    Assert-True ($reference.Groups['name'].Value -ceq $DomainName) 'Pattern reference identifies another domain name.'
    $form = Read-XmlPreservingWhitespace $State.Form
    $nativeVariables = ($State.Variables | ConvertFrom-Json).variables
    foreach ($variableName in @('Application', 'Simple')) {
        $declaration = @($nativeVariables | Where-Object { $_.name -ceq $variableName })
        Assert-True ($declaration.Count -eq 1 -and $null -ne $declaration[0].internalId) 'Native variable identity is unavailable.'
        $binding = 'var:' + $declaration[0].internalId
        Assert-True ($form.SelectNodes("//*[local-name()='data' and @attribute='$binding']").Count -eq 1) "Native layout variable binding missing: $variableName"
    }
    $buttons = $form.SelectNodes("//*[local-name()='action' and @controlName='BtnTestSave']")
    Assert-True ($buttons.Count -eq 1) 'Expected exactly one native TestSave button.'
    Assert-True ($buttons[0].GetAttribute('onClickEvent') -ceq "'DoTestSave'" -and $buttons[0].GetAttribute('caption') -ceq 'Save') 'Button caption or intended action event binding differs.'
    Assert-True ($State.Events -match 'UserOwnedCheck' -and $State.Events -match 'layout-sentinel') 'User event code was not preserved.'
}

function Assert-RollbackIfAttempted($Response) {
    $receipt = if ($null -ne $Response.result.saveAttempted) { $Response.result } else { $Response }
    if ($receipt.saveAttempted -eq $true -and $receipt.rollbackVerified -ne $true) {
        $script:workerStateUnknown = $true
        throw 'A failed native save did not independently verify rollback; automatic cleanup disabled.'
    }
}

$owners = @()
try {
    $worker = Start-TestWorker
    $domain = $prefix + 'Domain'
    $domainIdentity = New-OwnedObject $domain 'Domain' @{ dataType = 'Character'; length = 30 }
    foreach ($kind in @('WebPanel', 'WebComponent')) {
        $parent = $prefix + $kind
        $null = New-OwnedObject $parent 'WebPanel'
        $null = Invoke-Worker 'Write' 'AddVariable' $parent @{ varName = 'Application'; basedOn = $domain; validate = $false }
        $null = Invoke-Worker 'Write' 'AddVariable' $parent @{ varName = 'Simple'; typeName = 'VarChar'; length = 50; validate = $false }
        $variableContract = Get-VariableContract $parent $domainIdentity $domain
        $hostName = 'WorkWithPlus' + $parent
        $absentHost = Invoke-Worker 'Object' 'Read' $hostName @{ type = 'WorkWithPlus' } -AllowError
        Assert-True ((Get-ErrorCode $absentHost) -eq 'ObjectNotFound') 'Generated host absence was not proven.'
        $unattachedForm = [string] (Read-Part $parent 'WebForm').source
        $unattachedEvents = [string] (Read-Part $parent 'Events').source
        $missingTemplate = Invoke-Worker 'Pattern' 'Apply' $parent @{ pattern = 'WorkWithPlus'; settings = @{ template = $prefix + 'MissingTemplate' }; validate = $false } -AllowError
        Assert-True ((Get-ErrorCode $missingTemplate) -eq 'PatternTemplateNotFound') 'An invalid explicit template was not rejected before attach.'
        $stillAbsent = Invoke-Worker 'Object' 'Read' $hostName @{ type = 'WorkWithPlus' } -AllowError
        Assert-True ((Get-ErrorCode $stillAbsent) -eq 'ObjectNotFound') 'Invalid template unexpectedly attached a pattern.'
        Assert-True ($unattachedForm -ceq [string] (Read-Part $parent 'WebForm').source) 'Invalid template changed the parent form.'
        Assert-True ($unattachedEvents -ceq [string] (Read-Part $parent 'Events').source) 'Invalid template changed parent events.'
        Assert-VariableContract $variableContract (Get-VariableContract $parent $domainIdentity $domain)
        $apply = Invoke-Worker 'Pattern' 'Apply' $parent @{ pattern = 'WorkWithPlus'; settings = @{ template = 'Empty' }; validate = $false } -AllowError
        if ($apply.status -ne 'ok') {
            $preflightCodes = @('PatternTemplateNotFound', 'PatternTemplateUnsupported', 'PatternTemplateChangeUnsupported',
                'PatternUnavailable', 'PatternParentTypeUnsupported', 'PatternRouteUnsupported')
            if ((Get-ErrorCode $apply) -notin $preflightCodes -and $apply.status -ne 'pattern_unavailable') {
                $script:workerStateUnknown = $true
            }
            throw "Initial native attach failed; known preflight rejection permits owned-object cleanup, other outcomes need inspection: $($apply | ConvertTo-Json -Compress -Depth 30)"
        }
        $hostRead = Invoke-Worker 'Object' 'Read' $hostName @{ type = 'WorkWithPlus' }
        Assert-True (-not [string]::IsNullOrWhiteSpace($hostRead.result.identity.guid)) 'Generated host GUID unavailable.'
        $owned.Add(@{ Name = $hostName; Type = 'WorkWithPlus'; Guid = [string] $hostRead.result.identity.guid })
        $initial = Get-State $parent $hostName
        $initialPattern = Read-XmlPreservingWhitespace $initial.Pattern
        Assert-True ($initialPattern.SelectNodes("//*[local-name()='WPRoot' and @Template='Empty']").Count -eq 1) 'Explicit Empty template was not persisted.'
        $table = $initialPattern.SelectNodes("//*[local-name()='table' and @name='TableContent']")
        Assert-True ($table.Count -eq 1) 'Empty template did not expose one TableContent.'
        foreach ($unsupportedChange in @('TransactionTabs', 'View')) {
            $changedTemplate = Invoke-Worker 'Pattern' 'Apply' $parent @{ pattern = 'WorkWithPlus'; reapply = $true; settings = @{ template = $unsupportedChange }; validate = $false } -AllowError
            Assert-True ((Get-ErrorCode $changedTemplate) -in @('PatternTemplateChangeUnsupported', 'PatternTemplateNotFound')) 'Explicit template change was not honestly rejected before mutation.'
            Assert-SameState $initial (Get-State $parent $hostName) "Rejected explicit $unsupportedChange template"
        }

        # Author a synthetic user event through the guarded Events writer. No specify/build.
        $eventCode = $initial.Events + "`r`nEvent 'UserOwnedCheck'`r`n    &Simple = 'layout-sentinel'`r`nEndEvent`r`n"
        $eventWrite = Invoke-Worker 'Write' 'Events' $parent @{ content = $eventCode; validate = $false } -AllowError
        Assert-True ($eventWrite.status -eq 'ok') "Synthetic Events setup failed: $($eventWrite | ConvertTo-Json -Compress -Depth 20)"
        if ($kind -eq 'WebComponent') {
            // Seed user code through WebPanel before conversion. Do not accept or
            // retry an unknown Events save to finish preparing the test fixture.
            $seededEvents = [string] (Read-Part $parent 'Events').source
            $null = Invoke-Worker 'Property' 'Set' $parent @{ propertyName = 'WEB_COMP'; value = 'Yes' }
            Assert-True ((Invoke-Worker 'Property' 'Get' $parent @{ propertyName = 'WEB_COMP' }).result.value -eq 'Yes') 'WebComponent mode not persisted.'
            Assert-True ($seededEvents -ceq [string] (Read-Part $parent 'Events').source) 'Converting the synthetic object changed seeded Events.'
        }
        $before = Get-State $parent $hostName

        # Reproduce the generic structural edit guard without modifying the instance.
        $raw = Read-XmlPreservingWhitespace $before.Pattern
        $rawTable = $raw.SelectSingleNode("//*[local-name()='table' and @name='TableContent']")
        $rawVariable = $raw.CreateElement('variable')
        $rawVariable.SetAttribute('name', 'Application')
        $null = $rawTable.AppendChild($rawVariable)
        $guard = Invoke-Worker 'Write' 'PatternInstance' $hostName @{ content = $raw.OuterXml; dryRun = $true; expectedVersion = $before.Token; validate = $false } -AllowError
        Assert-True ((Get-ErrorCode $guard) -eq 'PatternStructureChangeUnsupported') 'Generic XML structural guard did not reject the edit.'
        Assert-SameState $before (Get-State $parent $hostName) 'Rejected generic edit'

        $children = @(@{ type = 'table'; name = 'TestFields'; tableType = 'Responsive'; children = @(
            @{ type = 'variable'; name = 'Application' }, @{ type = 'variable'; name = 'Simple' },
            @{ type = 'userAction'; name = 'TestSave'; caption = 'Save' }
        ) })
        $preview = Invoke-Worker 'WwpAction' 'Run' $hostName @{ action = 'add_layout'; tablePath = 'TableContent'; children = $children; expectedVersion = $before.Token; dryRun = $true }
        Assert-True ($preview.code -eq 'WwpLayoutDryRun' -or $preview.result.code -eq 'WwpLayoutDryRun') 'Unexpected layout preview result.'
        Assert-SameState $before (Get-State $parent $hostName) 'Native dryRun'

        $stale = Invoke-Worker 'WwpAction' 'Run' $hostName @{ action = 'add_layout'; tablePath = 'TableContent'; children = $children; expectedVersion = 'stale-token'; dryRun = $false } -AllowError
        Assert-True ((Get-ErrorCode $stale) -eq 'StaleObject') 'Stale layout mutation was not rejected.'
        Assert-SameState $before (Get-State $parent $hostName) 'Stale rejection'

        # A nested missing declaration must reject the entire plan without a partial table.
        $invalid = Invoke-Worker 'WwpAction' 'Run' $hostName @{ action = 'add_layout'; tablePath = 'TableContent'; expectedVersion = $before.Token; children = @(
            @{ type = 'table'; name = 'MustNotRemain'; children = @(@{ type = 'variable'; name = 'MissingDeclaration' }) }
        ) } -AllowError
        Assert-True ((Get-ErrorCode $invalid) -eq 'WwpVariableNotDeclared') 'Invalid nested variable did not retain its actionable preflight code.'
        Assert-RollbackIfAttempted $invalid
        Assert-SameState $before (Get-State $parent $hostName) 'Invalid nested batch'

        $saved = Invoke-Worker 'WwpAction' 'Run' $hostName @{ action = 'add_layout'; tablePath = 'TableContent'; children = $children; expectedVersion = $before.Token } -AllowError
        if ($saved.status -ne 'ok') {
            Assert-RollbackIfAttempted $saved
            Assert-SameState $before (Get-State $parent $hostName) 'Failed native authoring rollback'
            throw "Native layout save failed and original state was verified: $($saved | ConvertTo-Json -Compress -Depth 30)"
        }
        Assert-True ($saved.result.persisted -eq $true -and $saved.result.postSaveVerification.confirmed -eq $true) 'Native authoring did not prove persistence/projection.'
        $after = Get-State $parent $hostName
        Assert-LayoutPresent $after $domainIdentity $domain
        Assert-UserEventsPreserved $before.Events $after.Events
        Assert-VariableContract $variableContract (Get-VariableContract $parent $domainIdentity $domain)
        Assert-True ($before.Variables -ceq $after.Variables) 'Layout authoring changed a variable declaration/domain.'
        $domainType = (Invoke-Worker 'Property' 'Get' $parent @{ control = '&Application'; propertyName = 'DataTypeString' }).result.value
        Assert-True ($domainType -match [regex]::Escape($domain)) 'Domain binding changed during authoring.'

        $reapplied = Invoke-Worker 'Pattern' 'Apply' $parent @{ pattern = 'WorkWithPlus'; reapply = $true; validate = $false } -AllowError
        Assert-True ($reapplied.status -eq 'ok') "Native reapply did not complete: $($reapplied | ConvertTo-Json -Compress -Depth 30)"
        $reRead = Get-State $parent $hostName
        Assert-LayoutPresent $reRead $domainIdentity $domain
        Assert-UserEventsPreserved $before.Events $reRead.Events
        Assert-VariableContract $variableContract (Get-VariableContract $parent $domainIdentity $domain)
        Assert-True ($after.Pattern -ceq $reRead.Pattern -and $after.Variables -ceq $reRead.Variables) 'Reapply changed the authored pattern or declarations.'
        $owners += @{ Parent = $parent; HostName = $hostName; State = $reRead; DomainType = $domainType; VariableContract = $variableContract; OriginalEvents = $before.Events }
    }
    Stop-TestWorker $worker
    $worker = $null
    $worker = Start-TestWorker
    foreach ($owner in $owners) {
        $durable = Get-State $owner.Parent $owner.HostName
        Assert-SameState $owner.State $durable 'Worker restart'
        Assert-LayoutPresent $durable $domainIdentity $domain
        Assert-UserEventsPreserved $owner.OriginalEvents $durable.Events
        Assert-VariableContract $owner.VariableContract (Get-VariableContract $owner.Parent $domainIdentity $domain)
        $domainType = (Invoke-Worker 'Property' 'Get' $owner.Parent @{ control = '&Application'; propertyName = 'DataTypeString' }).result.value
        Assert-True ($domainType -ceq $owner.DomainType) 'Domain binding changed after restart.'
    }
    [pscustomobject] @{ Passed = $true; OwnerTypes = @('WebPanel', 'WebComponent'); WorkerReopens = 1;
        StructuralGuard = $true; DryRunUnchanged = $true; StaleRejected = $true; InvalidBatchUnchanged = $true;
        InvalidTemplateRejectedBeforeAttach = $true; ExistingTemplateChangesRejected = @('TransactionTabs', 'View'); LifecycleActions = @() }
}
finally {
    if ($null -ne $worker -and -not $workerStateUnknown) {
        for ($i = $owned.Count - 1; $i -ge 0; $i--) {
            $item = $owned[$i]
            try {
                $current = Invoke-Worker 'Object' 'Read' $item.Name @{ type = $item.Type }
                Assert-True ([string] $current.result.identity.guid -eq $item.Guid) 'Cleanup identity changed; refusing deletion.'
                $null = Invoke-Worker 'Object' 'Delete' $item.Name @{ type = $item.Type; confirm = $true }
            }
            catch { Write-Warning "Cleanup pending for $($item.Name): $($_.Exception.Message)"; if ($workerStateUnknown) { break } }
        }
    }
    elseif ($owned.Count -gt 0) { Write-Warning 'Unknown mutation state: automatic cleanup skipped; review synthetic object GUIDs in the evidence log.' }
    Stop-TestWorker $worker
}
