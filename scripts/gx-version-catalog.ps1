Set-StrictMode -Version Latest

function Get-GxVersionCatalog {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    $catalogPath = Join-Path ([IO.Path]::GetFullPath($Root)) 'config\gx-versions.json'
    if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
        throw "GeneXus version catalog was not found at '$catalogPath'."
    }

    try {
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    } catch {
        throw "GeneXus version catalog is invalid at '$catalogPath': $($_.Exception.Message)"
    }

    $primaryValue = if ($catalog.PSObject.Properties['primaryMajor']) { $catalog.primaryMajor } else { $null }
    if ([string]::IsNullOrWhiteSpace([string]$primaryValue)) {
        throw "GeneXus version catalog '$catalogPath' has no primaryMajor."
    }
    $entriesValue = if ($catalog.PSObject.Properties['supportedMajors']) { $catalog.supportedMajors } else { $null }
    $entries = if ($null -eq $entriesValue) { @() } else { @($entriesValue) }
    if (@($entries).Count -eq 0) {
        throw "GeneXus version catalog '$catalogPath' has no supportedMajors."
    }
    foreach ($entry in $entries) {
        $majorProperty = $entry.PSObject.Properties['major']
        if ($null -eq $majorProperty -or [string]::IsNullOrWhiteSpace([string]$majorProperty.Value)) {
            throw "GeneXus version catalog '$catalogPath' contains an entry without major."
        }
    }
    if (-not ($entries.major -contains [string]$primaryValue)) {
        throw "GeneXus version catalog '$catalogPath' primaryMajor '$primaryValue' is not listed in supportedMajors."
    }

    return $catalog
}

function Get-GxPrimaryMajor {
    param([Parameter(Mandatory = $true)][object]$Catalog)
    return [string]$Catalog.primaryMajor
}

function Get-GxPrimaryInstallPath {
    param([Parameter(Mandatory = $true)][object]$Catalog)
    $primary = @($Catalog.supportedMajors) |
        Where-Object { [string]$_.major -eq [string]$Catalog.primaryMajor } |
        Select-Object -First 1
    $pathValue = if ($null -ne $primary -and $primary.PSObject.Properties['defaultInstallPath']) {
        $primary.defaultInstallPath
    } else {
        $null
    }
    if ($null -eq $primary -or [string]::IsNullOrWhiteSpace([string]$pathValue)) {
        throw "GeneXus version catalog has no defaultInstallPath for primary major '$($Catalog.primaryMajor)'."
    }
    return [string]$pathValue
}

function Get-GxSupportedMajorsDisplay {
    param([Parameter(Mandatory = $true)][object]$Catalog)
    return ((@($Catalog.supportedMajors) | ForEach-Object { [string]$_.major }) -join ', ')
}
