param(
    [string]$CoverageRoot = "",
    [double]$MinLineRatePercent = 60,
    [double]$MinWorkerLineRatePercent = 45
)

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($CoverageRoot)) {
    if ($env:RUNNER_TEMP) {
        $CoverageRoot = Join-Path $env:RUNNER_TEMP "gx-coverage-artifacts"
    } else {
        $CoverageRoot = Join-Path $repoRoot "artifacts\coverage"
    }
}

function Get-LineRatePercent {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Coverage file not found: $Path"
    }

    [xml]$doc = Get-Content -LiteralPath $Path
    return [math]::Round(([double]$doc.coverage.'line-rate') * 100, 2)
}

$gatewayPath = Join-Path $CoverageRoot "gateway.cobertura.xml"
$workerPath = Join-Path $CoverageRoot "worker.cobertura.xml"
$workerSkippedMarker = Join-Path $CoverageRoot "worker.skipped.txt"
$workerFailedMarker = Join-Path $CoverageRoot "worker.failed.txt"

Write-Host "Required minimum (Gateway): $MinLineRatePercent%"

$gatewayRate = Get-LineRatePercent -Path $gatewayPath
Write-Host "Gateway line-rate: $gatewayRate%"

$failed = @()
if ($gatewayRate -lt $MinLineRatePercent) { $failed += "gateway=$gatewayRate%" }

# collect.ps1 branches on GeneXus-SDK presence: it emits worker.skipped.txt when
# the SDK isn't installed (every GitHub-hosted runner — see CONTRIBUTING.md) and
# worker.failed.txt when the Worker tests threw. Honor those markers instead of
# unconditionally reading worker.cobertura.xml and dying with a misleading
# "Coverage file not found" that hides the real cause.
if (Test-Path -LiteralPath $workerFailedMarker) {
    throw "Worker coverage collection failed (worker.failed.txt present). See the 'Gateway and Worker coverage' step log above for the dotnet test error."
} elseif (Test-Path -LiteralPath $workerSkippedMarker) {
    Write-Host "Worker line-rate: skipped (no local GeneXus 18 SDK; gateway threshold enforced only)." -ForegroundColor Yellow
} elseif (Test-Path -LiteralPath $workerPath) {
    $workerRate = Get-LineRatePercent -Path $workerPath
    Write-Host "Worker line-rate: $workerRate% (required: $MinWorkerLineRatePercent%)"
    if ($workerRate -lt $MinWorkerLineRatePercent) { $failed += "worker=$workerRate%" }
} else {
    throw "Worker coverage missing and no skip/failed marker present at $CoverageRoot. Expected worker.cobertura.xml, worker.skipped.txt, or worker.failed.txt from collect.ps1."
}

if ($failed.Count -gt 0) {
    throw "Coverage threshold failed: $($failed -join ', ')"
}

Write-Host "Coverage threshold satisfied."
