param(
    [Parameter(Mandatory = $true)][string]$GxPath,
    [Parameter(Mandatory = $true)][string]$Manifest
)

$ErrorActionPreference = 'Stop'
function Fail([string]$message) {
    Write-Error $message
    exit 1
}

if (-not (Test-Path -LiteralPath $GxPath -PathType Container)) {
    Fail "GXMCP_SDK_PATH_MISSING path=<missing>"
}
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    Fail "GXMCP_SDK_MANIFEST_MISSING manifest=$Manifest"
}
try { $spec = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json }
catch { Fail "GXMCP_SDK_MANIFEST_INVALID manifest=$Manifest error=Json" }

$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$manifestDirectory = Split-Path -Parent $manifestPath
$catalogCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($env:GXMCP_VERSION_CATALOG)) {
    $configuredCatalog = $env:GXMCP_VERSION_CATALOG.Trim().Trim('"')
    if (-not [IO.Path]::IsPathRooted($configuredCatalog)) {
        $configuredCatalog = Join-Path $manifestDirectory $configuredCatalog
    }
    $catalogCandidates += $configuredCatalog
}
$catalogCandidates += (Join-Path $manifestDirectory 'gx-versions.json')
$catalogCandidates += (Join-Path $manifestDirectory 'config\gx-versions.json')
$catalogCandidates += (Join-Path $manifestDirectory '..\..\config\gx-versions.json')
$catalogPath = $catalogCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $catalogPath) {
    Fail "GXMCP_SDK_CATALOG_MISSING catalog=$($catalogCandidates -join ';')"
}
try { $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json }
catch { Fail "GXMCP_SDK_CATALOG_INVALID catalog=$catalogPath" }
$supportedMajors = @($catalog.supportedMajors | ForEach-Object {
    $major = 0
    if ([int]::TryParse(([string]$_.major -split '\.')[0], [ref]$major) -and $major -gt 0) { $major }
} | Sort-Object -Unique)
if ($supportedMajors.Count -eq 0) {
    Fail "GXMCP_SDK_CATALOG_INVALID catalog=$catalogPath"
}

$expectedMajor = 0
if (-not [int]::TryParse(([string]$spec.supportedVersion -split '\.')[0], [ref]$expectedMajor) -or $expectedMajor -le 0) {
    Fail "GXMCP_SDK_MANIFEST_INVALID manifest=$Manifest error=supportedVersion"
}
$anchor = Join-Path $GxPath $spec.anchor
if (-not (Test-Path -LiteralPath $anchor -PathType Leaf)) {
    Fail "GXMCP_SDK_ANCHOR_MISSING path=$($spec.anchor)"
}
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($anchor).ProductVersion
$actualMajor = 0
$supportedMajor = [int]::TryParse(([string]$actualVersion -split '\.')[0], [ref]$actualMajor) -and
    $actualMajor -gt 0 -and $supportedMajors -contains $actualMajor
$exactVersion = $actualVersion -eq $spec.supportedVersion
if (-not $supportedMajor) {
    Fail "GXMCP_SDK_VERSION_MISMATCH expectedVersion=$($spec.supportedVersion) actualVersion=$actualVersion supportedMajors=$($supportedMajors -join ',')"
}
if (-not $spec.assemblies -or $spec.assemblies.Count -eq 0) {
    Fail "GXMCP_SDK_MANIFEST_INVALID manifest=$Manifest error=assemblies"
}
foreach ($assembly in $spec.assemblies) {
    $file = Join-Path $GxPath $assembly.path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        if ($assembly.optional -eq $true) { continue }
        Fail "GXMCP_SDK_ASSEMBLY_MISSING path=$($assembly.path)"
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($file)
        $actualHash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
    if ($actualHash -ne $assembly.sha256) {
        Write-Output "GXMCP_SDK_FINGERPRINT_DRIFT path=$($assembly.path) expectedSha256=$($assembly.sha256) actualSha256=$actualHash"
    }
}
if ($exactVersion) {
    $versionDiagnostic = $spec.supportedVersion
} elseif ($expectedMajor -eq $actualMajor) {
    $versionDiagnostic = "$($spec.supportedVersion) actualVersion=$actualVersion (compatible major; patch/build drift)"
} else {
    $versionDiagnostic = "$($spec.supportedVersion) actualVersion=$actualVersion (supported major $actualMajor; reference major $expectedMajor)"
}
Write-Output "GXMCP_SDK_COMPATIBLE version=$versionDiagnostic supportedMajors=$($supportedMajors -join ',') assemblies=$($spec.assemblies.Count)"
exit 0
