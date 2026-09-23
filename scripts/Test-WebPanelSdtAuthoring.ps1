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
$prefix = 'SdtCheck' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
if ($EvidenceFile) {
    if (Test-Path -LiteralPath $EvidenceFile) { throw 'EvidenceFile already exists; choose a fresh path.' }
    $null = New-Item -ItemType File -Path $EvidenceFile
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
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
    try { $Process.StandardInput.Close() } catch { }
    if (-not $Process.WaitForExit(15000)) { $Process.Kill(); $Process.WaitForExit() }
    $Process.Dispose()
}

function Invoke-Worker([string] $Method, [string] $Action, [string] $Target,
    [hashtable] $Arguments = @{}, [switch] $AllowError) {
    $script:requestNumber++
    $id = [string] $script:requestNumber
    $request = @{ jsonrpc = '2.0'; id = $id; method = $Method; action = $Action; target = $Target; params = $Arguments }
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
        if ($response.error) { throw "RPC $id failed: $($response.error.message)" }
        if ($response.result.status -ne 'ok' -and -not $AllowError) {
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

function Get-VariableProperty([string] $Target, [string] $Variable, [string] $Property) {
    $response = Invoke-Worker 'Property' 'Get' $Target @{ control = "&$Variable"; propertyName = $Property }
    return [string] $response.result.value
}

function Assert-NativeSdt([string] $Target, [string] $Variable, $Identity) {
    $raw = Get-VariableProperty $Target $Variable 'ATTCUSTOMTYPE'
    $offset = $raw.IndexOf('<')
    Assert-True ($offset -ge 0) "No native serialized SDT identity in $Target &$Variable"
    [xml] $reference = $raw.Substring($offset)
    Assert-True ([string] $reference.DocumentElement.LocalName -eq 'StructureTypeReference') 'Unexpected structural reference format.'
    Assert-True ([Guid] $reference.DocumentElement.Type -eq [Guid] $Identity.entityTypeGuid) 'Referenced native object class changed.'
    Assert-True ([int] $reference.DocumentElement.Id -eq [int] $Identity.entityId) 'Referenced native object ID changed.'
}

try {
    $worker = Start-TestWorker
    $domain = $prefix + 'Domain'
    $null = New-OwnedObject $domain 'Domain' @{ dataType = 'Character'; length = 30 }
    $sdts = @()
    foreach ($n in 1..3) {
        $name = $prefix + 'Record' + $n
        $identity = New-OwnedObject $name 'SDT'
        $sdts += @{ Name = $name; Identity = $identity }
    }
    $owners = @()
    foreach ($kind in @('WebPanel', 'WebComponent', 'Procedure')) {
        $target = $prefix + $kind
        # GeneXus represents a WebComponent as a WebPanel with Type=Component.
        $nativeType = if ($kind -eq 'Procedure') { 'Procedure' } else { 'WebPanel' }
        $null = New-OwnedObject $target $nativeType
        if ($kind -eq 'WebComponent') {
            $null = Invoke-Worker 'Property' 'Set' $target @{ propertyName = 'WEB_COMP'; value = 'Yes' }
            $component = Invoke-Worker 'Property' 'Get' $target @{ propertyName = 'WEB_COMP' }
            Assert-True ($component.result.value -eq 'Yes') 'WebComponent mode was not persisted.'
        }
        $null = Invoke-Worker 'Write' 'AddVariable' $target @{ varName = 'Application'; basedOn = $domain }
        $domainBefore = Get-VariableProperty $target 'Application' 'DataTypeString'
        Assert-True ($domainBefore -match [regex]::Escape($domain)) 'Domain was not bound before SDT writes.'
        foreach ($n in 0..2) {
            $null = Invoke-Worker 'Write' 'AddVariable' $target @{ varName = "Single$n"; typeName = $sdts[$n].Name }
            Assert-NativeSdt $target "Single$n" $sdts[$n].Identity
        }
        $batch = @(foreach ($n in 0..2) { @{ varName = "Batch$n"; typeName = $sdts[$n].Name } })
        $null = Invoke-Worker 'Write' 'AddVariable' $target @{ variables = $batch }
        foreach ($n in 0..2) { Assert-NativeSdt $target "Batch$n" $sdts[$n].Identity }
        $owners += @{ Name = $target; Kind = $kind; DomainBefore = $domainBefore }
    }
    Stop-TestWorker $worker
    $worker = $null
    $worker = Start-TestWorker
    foreach ($owner in $owners) {
        foreach ($n in 0..2) {
            Assert-NativeSdt $owner.Name "Single$n" $sdts[$n].Identity
            Assert-NativeSdt $owner.Name "Batch$n" $sdts[$n].Identity
        }
        Assert-True ((Get-VariableProperty $owner.Name 'Application' 'DataTypeString') -eq $owner.DomainBefore) 'Domain changed after restart.'
        $before = Invoke-Worker 'Read' 'GetVariables' $owner.Name
        $bad = Invoke-Worker 'Write' 'AddVariable' $owner.Name @{ variables = @(
            @{ varName = 'MustNotRemain'; typeName = $sdts[0].Name },
            @{ varName = 'InvalidReference'; typeName = ($prefix + 'MissingType') }
        ) } -AllowError
        Assert-True ($bad.status -eq 'error') 'Invalid mixed batch did not fail atomically.'
        $after = Invoke-Worker 'Read' 'GetVariables' $owner.Name
        Assert-True ($before.result.source -ceq $after.result.source) 'Invalid mixed batch left partial variables.'
        $owner.BeforeRejectedBatch = $before.result.source
    }
    Stop-TestWorker $worker
    $worker = $null
    $worker = Start-TestWorker
    foreach ($owner in $owners) {
        $read = Invoke-Worker 'Read' 'GetVariables' $owner.Name
        Assert-True ($read.result.source -ceq $owner.BeforeRejectedBatch) 'Rejected batch changed durable state after restart.'
        Assert-True ((Get-VariableProperty $owner.Name 'Application' 'DataTypeString') -eq $owner.DomainBefore) 'Rejected batch changed the domain.'
    }
    [pscustomobject] @{ Passed = $true; OwnerTypes = @($owners.Kind); NativeSdtBindings = 18; WorkerReopens = 2; InvalidBatchPreserved = $true; LifecycleActions = @() }
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
            catch {
                Write-Warning "Cleanup pending for $($item.Name): $($_.Exception.Message)"
                if ($workerStateUnknown) { break }
            }
        }
    }
    elseif ($owned.Count -gt 0) { Write-Warning 'Worker state is unknown; automatic cleanup skipped. Review owned synthetic objects in the evidence log.' }
    Stop-TestWorker $worker
}
