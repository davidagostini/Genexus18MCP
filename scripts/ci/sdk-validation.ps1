[CmdletBinding()]
param(
    [string]$GxPath = $env:GXMCP_SDK_PATH,
    [string]$KbPath = $env:GXMCP_TEST_KB,
    [string]$FixtureManifest = $env:GXMCP_TEST_FIXTURE,
    [string]$OutputRoot = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'gxmcp-sdk-validation' } else { Join-Path (Get-Location) 'artifacts\sdk-validation' })
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$statusPath = Join-Path $OutputRoot 'status.json'
$logPath = Join-Path $OutputRoot 'sdk-validation.log'

function Write-Status([string]$State, [string]$Reason, [int]$ExitCode) {
    $status = [ordered]@{
        schemaVersion = 1
        state = $State
        reason = $Reason
        exitCode = $ExitCode
        completedAtUtc = [datetime]::UtcNow.ToString('o')
    }
    $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    Write-Host "sdk-validation state=$State reason=$Reason"
}

function Skip-Lane([string]$Reason) {
    Write-Status 'skip' $Reason 0
    exit 0
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue

# The lane is opt-in on a protected runner. A local acknowledgement is a policy
# precondition, not a credential and not a substitute for the SDK fingerprint.
if ($env:GXMCP_SDK_CI_LICENSE_ACK -ne '1') {
    Skip-Lane 'GXMCP_SDK_CI_LICENSE_ACK=1 is required on the licensed self-hosted machine.'
}
if ([string]::IsNullOrWhiteSpace($GxPath) -or -not (Test-Path -LiteralPath $GxPath -PathType Container)) {
    Skip-Lane 'GeneXus SDK path is unavailable; set GXMCP_SDK_PATH on the protected runner.'
}
if ([string]::IsNullOrWhiteSpace($KbPath) -or [string]::IsNullOrWhiteSpace($FixtureManifest)) {
    Skip-Lane 'Verified disposable fixture is unavailable; set GXMCP_TEST_KB and GXMCP_TEST_FIXTURE.'
}
if (-not (Test-Path -LiteralPath $KbPath -PathType Container) -or -not (Test-Path -LiteralPath $FixtureManifest -PathType Leaf)) {
    Skip-Lane 'Configured fixture path or attestation manifest does not exist on this runner.'
}

$previous = @{}
foreach ($name in @('GX_PATH', 'GXMCP_TEST_KB', 'GXMCP_TEST_FIXTURE', 'GX_CONFIG_PATH')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}
$runDirectory = Join-Path $OutputRoot ('run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
try {
    $env:GX_PATH = (Resolve-Path -LiteralPath $GxPath).Path
    $env:GXMCP_TEST_KB = (Resolve-Path -LiteralPath $KbPath).Path
    $env:GXMCP_TEST_FIXTURE = (Resolve-Path -LiteralPath $FixtureManifest).Path
    $env:GX_CONFIG_PATH = Join-Path $runDirectory 'config.json'

    Write-Host "SDK validation runner prepared: sdk=$env:GX_PATH fixture=$env:GXMCP_TEST_KB"
    & pwsh -NoProfile -File (Join-Path $root 'scripts\validate-gx-sdk.ps1') `
        -GxPath $env:GX_PATH -Manifest (Join-Path $root 'config\sdk-compatibility.json') 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) { throw "SDK compatibility validator failed with exit code $LASTEXITCODE." }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\test-live.ps1') `
        -KbPath $env:GXMCP_TEST_KB -FixtureManifest $env:GXMCP_TEST_FIXTURE -GxPath $env:GX_PATH 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) { throw "Live Worker validation failed with exit code $LASTEXITCODE." }

    Write-Status 'pass' 'SDK fingerprint and live Worker validation completed.' 0
    exit 0
}
catch {
    $_ | Out-String | Tee-Object -FilePath $logPath -Append | Write-Host
    Write-Status 'fail' $_.Exception.Message 1
    exit 1
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name])
    }
    Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
