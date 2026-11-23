$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'CallActionPolicy.ps1')
$call = Join-Path $PSScriptRoot 'call.ps1'
$failures = New-Object System.Collections.Generic.List[string]

function Assert-Permitted([string]$Name, [string]$Tool, [string]$Json, [switch]$Hint) {
    $ok = Test-GxHubManualCallPermitted -ToolName $Tool -ArgumentsJson $Json -ReadOnlyHint:$Hint
    if (-not $ok) { $failures.Add("expected permit: $Name") }
}

function Assert-Denied([string]$Name, [string]$Tool, [string]$Json) {
    $ok = Test-GxHubManualCallPermitted -ToolName $Tool -ArgumentsJson $Json
    if ($ok) { $failures.Add("expected deny: $Name") }
}

Assert-Permitted 'hint bypasses grouped mutation' 'native_genexus_edit' '{"dryRun":false}' -Hint
Assert-Permitted 'journal_status exact' 'native_genexus_connection_recover' '{"action":"journal_status"}'
Assert-Permitted 'journal_status force false' 'native_genexus_connection_recover' '{"action":"journal_status","force":false}'
Assert-Permitted 'journal_repair explicit dryRun' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":true}'
Assert-Permitted 'journal_repair force false and dryRun' 'native_genexus_connection_recover' '{"force":false,"dryRun":true,"action":"journal_repair"}'
Assert-Permitted 'kb version list exact' 'native_genexus_kb_version' '{"action":"list"}'
Assert-Permitted 'properties get by name' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","type":"WebPanel","propertyNames":["MasterPage"]}'
Assert-Permitted 'properties get one property' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","propertyName":"MasterPage"}'
Assert-Permitted 'properties get projection query kb control' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","type":"WebPanel","query":"Master*","projection":"minimal","kb":"synth","control":"SynthControl"}'
Assert-Permitted 'properties get action last' 'native_genexus_properties' '{"name":"SynthPanel","propertyNames":["MasterPage","Description"],"action":"get"}'
Assert-Permitted 'properties get empty names' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","propertyNames":[]}'

Assert-Denied 'unprefixed tool' 'genexus_connection_recover' '{"action":"journal_status"}'
Assert-Denied 'similar tool name' 'native_genexus_connection_recover_extra' '{"action":"journal_status"}'
Assert-Denied 'tool casing' 'Native_Genexus_Connection_Recover' '{"action":"journal_status"}'
Assert-Denied 'action casing' 'native_genexus_connection_recover' '{"action":"Journal_Status"}'
Assert-Denied 'field Action casing' 'native_genexus_connection_recover' '{"Action":"journal_status"}'
Assert-Denied 'field DryRun casing' 'native_genexus_connection_recover' '{"action":"journal_repair","DryRun":true}'
Assert-Denied 'force true with dryRun true' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":true,"force":true}'
Assert-Denied 'action omitted' 'native_genexus_connection_recover' '{}'
Assert-Denied 'action null' 'native_genexus_connection_recover' '{"action":null}'
Assert-Denied 'action invalid' 'native_genexus_connection_recover' '{"action":"recover"}'
Assert-Denied 'force true on status' 'native_genexus_connection_recover' '{"action":"journal_status","force":true}'
Assert-Denied 'dryRun omitted on repair' 'native_genexus_connection_recover' '{"action":"journal_repair"}'
Assert-Denied 'dryRun false' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":false}'
Assert-Denied 'dryRun string true' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":"true"}'
Assert-Denied 'dryRun null' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":null}'
Assert-Denied 'unknown extra' 'native_genexus_connection_recover' '{"action":"journal_status","kb":"x"}'
Assert-Denied 'dryRun extra on status' 'native_genexus_connection_recover' '{"action":"journal_status","dryRun":true}'
Assert-Denied 'array' 'native_genexus_connection_recover' '[{"action":"journal_status"}]'
Assert-Denied 'scalar' 'native_genexus_connection_recover' '"journal_status"'
Assert-Denied 'duplicate action' 'native_genexus_connection_recover' '{"action":"journal_status","action":"recover"}'
Assert-Denied 'set_active' 'native_genexus_kb_version' '{"action":"set_active"}'
Assert-Denied 'list force false' 'native_genexus_kb_version' '{"action":"list","force":false}'
Assert-Denied 'list dryRun' 'native_genexus_kb_version' '{"action":"list","dryRun":true}'
Assert-Denied 'force string on status' 'native_genexus_connection_recover' '{"action":"journal_status","force":"true"}'
Assert-Denied 'kb list extra' 'native_genexus_kb_version' '{"action":"list","name":"UpgradesVN"}'
Assert-Denied 'status plus wait' 'native_genexus_connection_recover' '{"action":"journal_status","wait":30}'
Assert-Denied 'status plus freshness' 'native_genexus_connection_recover' '{"action":"journal_status","freshness":"current"}'
Assert-Denied 'repair plus wait' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":true,"wait":30}'
Assert-Denied 'repair plus freshness' 'native_genexus_connection_recover' '{"action":"journal_repair","dryRun":true,"freshness":"current"}'
Assert-Denied 'list plus wait' 'native_genexus_kb_version' '{"action":"list","wait":30}'
Assert-Denied 'list plus freshness' 'native_genexus_kb_version' '{"action":"list","freshness":"current"}'
Assert-Permitted 'lifecycle status exact' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current"}'
Assert-Denied 'lifecycle force false' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current","force":false}'
Assert-Denied 'lifecycle dryRun true' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current","dryRun":true}'
Assert-Denied 'lifecycle status only' 'native_genexus_lifecycle' '{"action":"status"}'
Assert-Denied 'lifecycle wait other' 'native_genexus_lifecycle' '{"action":"status","wait":31,"freshness":"current"}'
Assert-Denied 'lifecycle wait decimal' 'native_genexus_lifecycle' '{"action":"status","wait":30.0,"freshness":"current"}'
Assert-Denied 'lifecycle wait string' 'native_genexus_lifecycle' '{"action":"status","wait":"30","freshness":"current"}'
Assert-Denied 'lifecycle freshness casing' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"Current"}'
Assert-Denied 'lifecycle action casing' 'native_genexus_lifecycle' '{"action":"Status","wait":30,"freshness":"current"}'
Assert-Denied 'lifecycle target extra' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current","target":"op:1"}'
Assert-Denied 'lifecycle job id' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current","jobId":"1"}'
Assert-Denied 'lifecycle since extra' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current","since":"Ready"}'
Assert-Denied 'lifecycle duplicate' 'native_genexus_lifecycle' '{"action":"status","wait":30,"freshness":"current","freshness":"current"}'
Assert-Denied 'lifecycle index' 'native_genexus_lifecycle' '{"action":"index"}'
Assert-Denied 'lifecycle warm' 'native_genexus_lifecycle' '{"action":"warm"}'
Assert-Denied 'edit dryRun' 'native_genexus_edit' '{"dryRun":true}'
Assert-Denied 'malformed' 'native_genexus_connection_recover' '{action:journal_status}'
Assert-Denied 'properties set' 'native_genexus_properties' '{"action":"set","name":"SynthPanel","propertyName":"MasterPage","value":"SynthMaster"}'
Assert-Denied 'properties move' 'native_genexus_properties' '{"action":"move","name":"SynthPanel","destination":"SynthModule"}'
Assert-Denied 'properties action omitted' 'native_genexus_properties' '{"name":"SynthPanel"}'
Assert-Denied 'properties action casing' 'native_genexus_properties' '{"action":"Get","name":"SynthPanel"}'
Assert-Denied 'properties field casing' 'native_genexus_properties' '{"Action":"get","name":"SynthPanel"}'
Assert-Denied 'properties get plus value' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","value":"x"}'
Assert-Denied 'properties get plus properties map' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","properties":{"MasterPage":"x"}}'
Assert-Denied 'properties get plus expectedVersion' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","expectedVersion":"1:x"}'
Assert-Denied 'properties get plus baseVersion' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","baseVersion":"1:x"}'
Assert-Denied 'properties get plus dryRun' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","dryRun":true}'
Assert-Denied 'properties get plus rollbackOnFailure' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","rollbackOnFailure":true}'
Assert-Denied 'properties get reconcile true' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","reconcileTimedOutWrite":true}'
Assert-Denied 'properties get reconcile false' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","reconcileTimedOutWrite":false}'
Assert-Denied 'properties get batch targets' 'native_genexus_properties' '{"action":"get","targets":[{"name":"SynthPanel"}]}'
Assert-Denied 'properties get names not array' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","propertyNames":"MasterPage"}'
Assert-Denied 'properties get names with number' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","propertyNames":["MasterPage",1]}'
Assert-Denied 'properties get name not string' 'native_genexus_properties' '{"action":"get","name":1}'
Assert-Denied 'properties get action null' 'native_genexus_properties' '{"action":null,"name":"SynthPanel"}'
Assert-Denied 'properties get duplicate action' 'native_genexus_properties' '{"action":"get","action":"set","name":"SynthPanel"}'
Assert-Denied 'properties get duplicate name' 'native_genexus_properties' '{"action":"get","name":"SynthPanel","name":"SynthOther"}'
Assert-Denied 'properties get array root' 'native_genexus_properties' '[{"action":"get"}]'
Assert-Denied 'properties get malformed' 'native_genexus_properties' '{action:get}'
Assert-Denied 'properties similar tool name' 'native_genexus_properties_extra' '{"action":"get","name":"SynthPanel"}'
Assert-Denied 'properties unprefixed tool' 'genexus_properties' '{"action":"get","name":"SynthPanel"}'
Assert-Denied 'properties tool casing' 'Native_Genexus_Properties' '{"action":"get","name":"SynthPanel"}'

$probeCases = @(
    @{ Name = 'probe lifecycle'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","wait":30,"freshness":"current"}'; Expect = 0 },
    @{ Name = 'probe lifecycle extra'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","wait":30,"freshness":"current","target":"x"}'; Expect = 2 },
    @{ Name = 'probe lifecycle wait'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","wait":31,"freshness":"current"}'; Expect = 2 },
    @{ Name = 'probe lifecycle no wait'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","freshness":"current"}'; Expect = 2 },
    @{ Name = 'probe lifecycle no freshness'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","wait":30}'; Expect = 2 },
    @{ Name = 'probe lifecycle job'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","wait":30,"freshness":"current","jobId":"1"}'; Expect = 2 },
    @{ Name = 'probe lifecycle case'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"status","wait":30,"freshness":"Current"}'; Expect = 2 },
    @{ Name = 'probe lifecycle index'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"index"}'; Expect = 2 },
    @{ Name = 'probe lifecycle warm'; Tool = 'native_genexus_lifecycle'; Json = '{"action":"warm"}'; Expect = 2 },
    @{ Name = 'probe set_active'; Tool = 'native_genexus_kb_version'; Json = '{"action":"set_active"}'; Expect = 2 },
    @{ Name = 'probe edit dryRun'; Tool = 'native_genexus_edit'; Json = '{"dryRun":true}'; Expect = 2 },
    @{ Name = 'probe status'; Json = '{"action":"journal_status"}'; Expect = 0 },
    @{ Name = 'probe repair'; Json = '{"action":"journal_repair","dryRun":true}'; Expect = 0 },
    @{ Name = 'probe force'; Json = '{"action":"journal_status","force":true}'; Expect = 2 },
    @{ Name = 'probe properties get'; Tool = 'native_genexus_properties'; Json = '{"action":"get","name":"SynthPanel","propertyNames":["MasterPage"]}'; Expect = 0 },
    @{ Name = 'probe properties set'; Tool = 'native_genexus_properties'; Json = '{"action":"set","name":"SynthPanel","propertyName":"MasterPage","value":"x"}'; Expect = 2 },
    @{ Name = 'probe properties get reconcile'; Tool = 'native_genexus_properties'; Json = '{"action":"get","name":"SynthPanel","reconcileTimedOutWrite":true}'; Expect = 2 },
    @{ Name = 'probe hint'; Json = '{"dryRun":false}'; Expect = 0; Hint = $true }
)
foreach ($case in $probeCases) {
    $tool = if ($case.ContainsKey('Tool') -and $case.Tool) { $case.Tool } else { 'native_genexus_connection_recover' }
    if ($case.Hint) { $tool = 'native_genexus_edit' }
    $probeArgs = @('-NoProfile', '-File', $call, '-PolicyProbe', '-Tool', $tool, '-ArgumentsJson', [string]$case.Json)
    if ($case.Hint) { $probeArgs += '-ProbeReadOnlyHint' }
    $output = (& pwsh @probeArgs | Out-String).Trim()
    $code = $LASTEXITCODE
    $expectedText = if ($case.Expect -eq 0) { 'permitted' } else { 'allow-write-required' }
    if ($code -ne $case.Expect -or $output -ne $expectedText) {
        $failures.Add("$($case.Name) exit $code output $output")
    }
}

$text = Get-Content -LiteralPath $call -Raw
$guardHits = [regex]::Matches($text, 'Test-GxHubManualCallPermitted -ToolName \$Tool').Count
$prod = $text.IndexOf('$permittedWithoutAllowWrite = Test-GxHubManualCallPermitted')
$invoke = $text.IndexOf("method='tools/call'")
if ($guardHits -lt 2 -or $prod -lt 0 -or $invoke -lt 0 -or $prod -gt $invoke) {
    $failures.Add('production guard is not before tools/call')
}
if ($text -notmatch [regex]::Escape('$readOnly = $definition.annotations.readOnlyHint -eq $true')) {
    $failures.Add('readOnly does not come from the catalog annotation')
}
if ($text -notmatch [regex]::Escape('-ReadOnlyHint:$readOnly')) {
    $failures.Add('production guard does not pass the catalog hint')
}
$condAt = $text.IndexOf('if (-not $permittedWithoutAllowWrite -and -not $AllowWrite)')
if ($condAt -lt 0) {
    $failures.Add('AllowWrite condition is missing')
} else {
    $window = $text.Substring($condAt, [Math]::Min(280, $text.Length - $condAt))
    if ($window -notmatch 'throw "A tool') {
        $failures.Add('AllowWrite condition has no throw')
    }
}
$probeHintHits = [regex]::Matches($text, '\$ProbeReadOnlyHint').Count
if ($probeHintHits -ne 2) {
    $failures.Add("ProbeReadOnlyHint count $probeHintHits")
}
if ($text -notmatch 'Join-Path \$PSScriptRoot ''CallActionPolicy.ps1''') {
    $failures.Add('call.ps1 does not load CallActionPolicy.ps1')
}

if ($failures.Count) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "passed"
exit 0
