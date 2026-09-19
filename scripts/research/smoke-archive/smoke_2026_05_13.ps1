# Smoke test for the 2026-05-13 friction-report fixes.
# Drives the gateway via HTTP JSON-RPC at /mcp so it works even when the
# Claude Code MCP client isn't connected. Prints PASS/FAIL per fix and exits
# non-zero on any failure.

$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:5000/mcp'
$proto = '2025-11-25'
$session = $null

function Send-Rpc {
    param([string]$Method, [hashtable]$Params = @{}, $Id = 0, [switch]$IsNotification)
    $payload = @{ jsonrpc='2.0'; method=$Method; params=$Params }
    if (-not $IsNotification) { $payload['id'] = $Id }
    $body = $payload | ConvertTo-Json -Depth 20 -Compress
    $headers = @{ 'Content-Type'='application/json'; 'Accept'='application/json, text/event-stream'; 'MCP-Protocol-Version'=$proto }
    if ($script:session) { $headers['MCP-Session-Id'] = $script:session }
    $resp = Invoke-WebRequest -Uri $base -Method POST -Body $body -Headers $headers -UseBasicParsing -TimeoutSec 600 -SkipHttpErrorCheck
    foreach ($pair in $resp.Headers.GetEnumerator()) {
        if ($pair.Key -ieq 'MCP-Session-Id') {
            $val = $pair.Value
            if ($val -is [array]) { $val = $val[0] }
            if (-not [string]::IsNullOrWhiteSpace($val)) { $script:session = $val }
        }
    }
    if ($IsNotification) { return $null }
    $text = $resp.Content
    if ($text -match '(?ms)^data:\s*(\{.*?\})\s*$') { $text = $matches[1] }
    return ($text | ConvertFrom-Json)
}

function Call-Tool {
    param([string]$Name, [hashtable]$ToolArgs = @{}, [int]$Id = 100)
    $r = Send-Rpc -Method 'tools/call' -Params @{ name=$Name; arguments=$ToolArgs } -Id $Id
    if ($r.error) { throw "tools/call $Name returned error: $($r.error.message)" }
    $text = $r.result.content[0].text
    return ($text | ConvertFrom-Json)
}

# 1) Initialize
$init = Send-Rpc -Method 'initialize' -Params @{
    protocolVersion = $proto
    capabilities = @{}
    clientInfo = @{ name='smoke-2026-05-13'; version='1.0' }
} -Id 1
Write-Host "[init] sessionId=$script:session protoVer=$($init.result.protocolVersion)"
Send-Rpc -Method 'notifications/initialized' -Params @{} -IsNotification | Out-Null

$results = New-Object System.Collections.ArrayList
function Mark { param($name,$ok,$detail) [void]$results.Add([pscustomobject]@{ Fix=$name; Pass=$ok; Detail=$detail }) ; if (-not $ok) { Write-Host "FAIL $name :: $detail" -ForegroundColor Red } else { Write-Host "OK   $name :: $detail" -ForegroundColor Green } }

# Probe whoami (Fix #1)
$who = Call-Tool -Name 'genexus_whoami' -ToolArgs @{} -Id 10
Mark 'fix1-whoami-version' ($who.mcp.serverVersion -eq '2.1.6') "serverVersion=$($who.mcp.serverVersion)"

# Cleanup any leftover probes from a prior run
foreach ($n in 'PrcFrictionProbe0513V','PrcFrictionProbe0513W','PrcFrictionProbe0513X','SdtFrictionProbe0513V') {
    try {
        $ty = if ($n.StartsWith('Sdt')) { 'SDT' } else { 'Procedure' }
        Call-Tool -Name 'genexus_delete_object' -ToolArgs @{ name=$n; type=$ty; confirm=$true } -Id 11 | Out-Null
    } catch { }
}

# Fix #8: create_object SDT announces seed in _meta
$sdtName = 'SdtFrictionProbe0513V'
$created = Call-Tool -Name 'genexus_create_object' -ToolArgs @{ type='SDT'; name=$sdtName } -Id 20
$seeded = $created._meta.seeded -join ','
Mark 'fix8-create-seed-meta' ($created._meta -and $seeded -match 'Item1') "_meta.seeded=[$seeded]"

# Fix #2 setup: write structure DSL and verify round-trip pasta validador
$dsl = "AluCod : NUMERIC(8,0)`nAluNom : CHARACTER(60)`nAluAtv : CHARACTER(1)"
$writeStruct = Call-Tool -Name 'genexus_edit' -ToolArgs @{ name=$sdtName; part='Structure'; mode='full'; content=$dsl } -Id 21
$pv = $writeStruct.persistedVerified
$pvErr = $writeStruct.persistedVerifyError
Mark 'fix2-structure-persistedVerified-flag' ($pv -ne $null) "persistedVerified field present (=$pv) err='$pvErr'"

# Fix #7: inspect SDT exposes items
$insp = Call-Tool -Name 'genexus_inspect' -ToolArgs @{ name=$sdtName; include=@('structure') } -Id 22
$itemCount = if ($insp.sdtStructure) { $insp.sdtStructure.itemCount } else { -1 }
Mark 'fix7-inspect-sdt-items' ($itemCount -ge 1) "sdtStructure.itemCount=$itemCount items=$($insp.sdtStructure.items | ConvertTo-Json -Compress -Depth 5)"

# Fix #2 hard assert: try to USE the SDT in a procedure source. Validator should accept the fields.
$procName = 'PrcFrictionProbe0513V'
Call-Tool -Name 'genexus_create_object' -ToolArgs @{ type='Procedure'; name=$procName } -Id 23 | Out-Null
Call-Tool -Name 'genexus_add_variable' -ToolArgs @{ name=$procName; varName='&Aluno'; typeName=$sdtName } -Id 24 | Out-Null
$src = "&Aluno.AluCod = 42`n&Aluno.AluNom = `"x`"`n&Aluno.AluAtv = `"A`""
$writeSrc = $null; $writeSrcErr = $null
try { $writeSrc = Call-Tool -Name 'genexus_edit' -ToolArgs @{ name=$procName; part='Source'; mode='full'; content=$src } -Id 25 }
catch { $writeSrcErr = $_.Exception.Message }
$ok2 = ($writeSrc -and $writeSrc.status -eq 'Success' -and -not $writeSrc.error)
Mark 'fix2-sdt-consumable-by-validator' $ok2 "writeSrc.status=$($writeSrc.status) err='$($writeSrc.error)'$writeSrcErr"

# Fix #4: Variables patch Append with NUMERIC(N,0) round-trips clean on a Procedure
# that already consumes an SDT — this is the original report's exact scenario.
$writeVars = Call-Tool -Name 'genexus_edit' -ToolArgs @{ name=$procName; part='Variables'; mode='patch'; operation='Append'; content='&Counter : NUMERIC(4,0)' } -Id 26
$vOk = ($writeVars.persistedVerified -eq $true -and $writeVars.patchStatus -eq 'Applied')
Mark 'fix4-variables-patch-on-sdt-consumer' $vOk "persistedVerified=$($writeVars.persistedVerified) patchStatus=$($writeVars.patchStatus) error='$($writeVars.error)'"

# Fix #3: src0216 enriched with undeclared-var hint
$procB = 'PrcFrictionProbe0513W'
Call-Tool -Name 'genexus_create_object' -ToolArgs @{ type='Procedure'; name=$procB } -Id 27 | Out-Null
$errResp = $null
try { $errResp = Call-Tool -Name 'genexus_edit' -ToolArgs @{ name=$procB; part='Source'; mode='full'; content="&Externo.Id = 1`nmsg(&Externo.Nome)" } -Id 28 }
catch { Mark 'fix3-src0216-undeclared-var-hint' $false "throw: $($_.Exception.Message)"; $errResp = $null }
if ($errResp) {
    $hint = $errResp.hint
    $unArr = if ($errResp.undeclaredVariables) { $errResp.undeclaredVariables -join ',' } else { '' }
    $ok3 = ($hint -and $hint.Contains('undeclared') -and $unArr.Contains('&Externo'))
    Mark 'fix3-src0216-undeclared-var-hint' $ok3 "hint='$hint' undeclaredVariables=[$unArr]"
}

# Fix #5: lifecycle build echoes targets for single target
$buildResp = Call-Tool -Name 'genexus_lifecycle' -ToolArgs @{ action='build'; target=$procName } -Id 29
$tgts = if ($buildResp.targets) { $buildResp.targets -join ',' } else { '' }
Mark 'fix5-build-targets-echo' ($tgts -eq $procName) "targets=[$tgts]"

# Fix #6: lifecycle status TailLines free of mojibake (no double-replacement chars)
Start-Sleep -Seconds 4
$st = Call-Tool -Name 'genexus_lifecycle' -ToolArgs @{ action='status'; target=$buildResp.taskId } -Id 30
$tail = ($st.TailLines -join "`n")
$mojibake = ($tail -match '��')
Mark 'fix6-build-output-encoding' (-not $mojibake) ("TailLines first=" + ($st.TailLines | Select-Object -First 1))

# Cancel build to release the KB
try { Call-Tool -Name 'genexus_lifecycle' -ToolArgs @{ action='cancel'; target=$buildResp.taskId } -Id 31 | Out-Null } catch {}

# Cleanup
if ($env:SMOKE_KEEP_PROBES -ne '1') {
foreach ($n in @($procName,$procB)) {
    try { Call-Tool -Name 'genexus_delete_object' -ToolArgs @{ name=$n; type='Procedure'; confirm=$true } -Id 40 | Out-Null } catch {}
}
try { Call-Tool -Name 'genexus_delete_object' -ToolArgs @{ name=$sdtName; type='SDT'; confirm=$true } -Id 41 | Out-Null } catch {}
} else { Write-Host "[smoke] SMOKE_KEEP_PROBES=1 — skipping cleanup" -ForegroundColor Yellow }

Write-Host ""
Write-Host "=== Smoke summary ==="
$results | Format-Table Fix, Pass, Detail -AutoSize -Wrap
$failCount = ($results | Where-Object { -not $_.Pass }).Count
if ($failCount -gt 0) { Write-Host "$failCount failure(s)." -ForegroundColor Red; exit 1 } else { Write-Host "All fixes verified." -ForegroundColor Green; exit 0 }
