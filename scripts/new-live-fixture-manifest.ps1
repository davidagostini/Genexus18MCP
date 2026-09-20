[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KbPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')][string]$FixtureId,
    [Parameter(Mandatory = $true)][string]$FixtureRevision,
    [Parameter(Mandatory = $true)][string]$Generator,
    [Parameter(Mandatory = $true)][string]$KbDatabaseId,
    [Parameter(Mandatory = $true)][string]$ApplicationDatabaseId,
    [Parameter(Mandatory = $true)][string]$Evidence,
    [Parameter(Mandatory = $true)][ValidateSet('GeneXus', 'XPZ')][string]$ProvisionedBy,
    [Parameter(Mandatory = $true)][switch]$ConfirmIsolated,
    [switch]$Force,
    [string]$OutputPath,
    [string]$VerifiedAt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\live-fixture.ps1')

if (-not $ConfirmIsolated) {
    throw 'Manifest generation requires -ConfirmIsolated after independently verifying dedicated KB and application databases.'
}
if (-not [IO.Path]::IsPathRooted($KbPath) -or -not (Test-Path -LiteralPath $KbPath -PathType Container)) {
    throw "KB directory not found or not absolute: $KbPath"
}
$resolvedKbPath = (Resolve-Path -LiteralPath $KbPath).Path
if ([string]::IsNullOrWhiteSpace($FixtureRevision) -or [string]::IsNullOrWhiteSpace($Generator) -or
    [string]::IsNullOrWhiteSpace($KbDatabaseId) -or [string]::IsNullOrWhiteSpace($ApplicationDatabaseId) -or
    [string]::IsNullOrWhiteSpace($Evidence)) {
    throw 'Fixture revision, generator, database IDs, and evidence are required and must not be blank.'
}

$gxwFiles = @(Get-ChildItem -LiteralPath $resolvedKbPath -Filter '*.gxw' -File -ErrorAction Stop)
if ($gxwFiles.Count -ne 1) {
    throw "Fixture must contain exactly one .gxw workspace file; found $($gxwFiles.Count)."
}
$connectionFile = Join-Path $resolvedKbPath 'knowledgebase.connection'
if (-not (Test-Path -LiteralPath $connectionFile -PathType Leaf)) {
    throw 'Fixture knowledgebase.connection is required.'
}

if ([string]::IsNullOrWhiteSpace($VerifiedAt)) { $VerifiedAt = [DateTimeOffset]::UtcNow.ToString('o') }
$verifiedAtValue = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($VerifiedAt, [ref]$verifiedAtValue) -or $verifiedAtValue -gt [DateTimeOffset]::UtcNow) {
    throw "VerifiedAt is not a valid non-future timestamp: $VerifiedAt"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "scratchpad\$FixtureId.fixture.json"
} elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $root $OutputPath
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ((Test-Path -LiteralPath $OutputPath -PathType Leaf) -and -not $Force) {
    throw "Manifest already exists: $OutputPath. Use -Force only when replacing this exact local fixture record."
}
$outputParent = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}

$manifest = [ordered]@{
    schemaVersion = 1
    fixtureId = $FixtureId
    fixtureRevision = $FixtureRevision
    generator = $Generator
    kbPath = $resolvedKbPath
    synthetic = $true
    disposable = $true
    isolation = [ordered]@{
        verified = $true
        kbDatabaseId = $KbDatabaseId
        applicationDatabaseId = $ApplicationDatabaseId
        evidence = $Evidence
        provisionedBy = $ProvisionedBy
        verifiedAt = $verifiedAtValue.ToUniversalTime().ToString('o')
    }
    provenance = [ordered]@{
        gxwSha256 = Get-LiveFixtureHash $gxwFiles[0].FullName -NormalizeGxw
        connectionSha256 = Get-LiveFixtureHash $connectionFile
    }
}
$manifestObject = [pscustomobject]$manifest
Assert-LiveFixture $manifestObject $resolvedKbPath

$temporaryPath = "$OutputPath.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $json = $manifestObject | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    if ($Force -and (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
    } else {
        [IO.File]::Move($temporaryPath, $OutputPath)
    }
} finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}
Write-Output $OutputPath
