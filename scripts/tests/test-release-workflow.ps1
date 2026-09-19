$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workflowPath = Join-Path $root '.github/workflows/release.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw

function Assert-Workflow([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Release workflow contract failed: $Message" }
}

Assert-Workflow ($workflow -match 'npm install --force --no-audit --no-fund') 'npm install must skip audit and fund network work.'
Assert-Workflow ($workflow -match '--fetch-retries=0') 'npm registry probes must not hide retries inside one probe.'
Assert-Workflow ($workflow -match '--fetch-timeout=5000') 'npm registry probes must have a bounded request timeout.'
Assert-Workflow ($workflow -match 'if \[ "\$i" -lt "\$MAX_ATTEMPTS" \]; then') 'polling must not sleep after its final attempt.'
Assert-Workflow ($workflow -match 'GITHUB_STEP_SUMMARY') 'npm publication timing must be visible in the workflow summary.'
Assert-Workflow ($workflow -match 'PROPAGATION_SECONDS') 'registry propagation duration must be measured.'
Assert-Workflow ($workflow -match 'PUBLISH_ACCEPTED_EPOCH') 'publish acceptance timestamp must be captured.'
Assert-Workflow ($workflow -match 'REGISTRY_VISIBLE_EPOCH') 'registry visibility timestamp must be captured.'

$exactQueryCount = ([regex]::Matches($workflow, 'npm view "\$\{TARGET_SPEC\}" version')).Count
Assert-Workflow ($exactQueryCount -ge 1) 'the verification query must use the exact package@version spec.'

Write-Host 'release-workflow: bounded npm probes, no final sleep and propagation telemetry passed' -ForegroundColor Green
