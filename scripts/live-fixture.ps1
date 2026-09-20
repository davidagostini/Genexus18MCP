function Get-LiveFixtureHash([string]$Path, [switch]$NormalizeGxw) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($NormalizeGxw) {
        $text = ([Text.Encoding]::UTF8.GetString($bytes)).TrimStart([char]0xFEFF)
        try {
            $xml = [xml]$text
            foreach ($elementName in @('FriendlyVersion', 'VersionNumber')) {
                $nodes = $xml.SelectNodes("//*[local-name()='$elementName']")
                foreach ($node in $nodes) { $node.InnerText = '' }
            }
            $text = $xml.OuterXml
        }
        catch { }
        $text = ($text -replace "`r`n", "`n" -replace "`r", "`n").Trim()
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-LiveFixture($Fixture, [string]$ResolvedKbPath) {
    if ($Fixture.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($Fixture.fixtureId) -or
        [string]::IsNullOrWhiteSpace($Fixture.fixtureRevision) -or
        [string]::IsNullOrWhiteSpace($Fixture.generator) -or
        $Fixture.synthetic -isnot [bool] -or -not $Fixture.synthetic -or
        $Fixture.disposable -isnot [bool] -or -not $Fixture.disposable) {
        throw 'Fixture must identify a synthetic, disposable KB with fixtureRevision and generator using schemaVersion 1.'
    }
    if ([string]::IsNullOrWhiteSpace($Fixture.kbPath) -or
        -not [IO.Path]::IsPathRooted($Fixture.kbPath) -or
        [IO.Path]::GetFullPath($Fixture.kbPath).TrimEnd('\') -ine [IO.Path]::GetFullPath($ResolvedKbPath).TrimEnd('\')) {
        throw 'Fixture kbPath must match the explicitly selected KB.'
    }
    $isolation = $Fixture.isolation
    if ($isolation.verified -isnot [bool] -or -not $isolation.verified) {
        throw 'Fixture requires verified database isolation, not a copied KB directory.'
    }
    foreach ($field in @('kbDatabaseId', 'applicationDatabaseId', 'evidence', 'provisionedBy', 'verifiedAt')) {
        if ([string]::IsNullOrWhiteSpace($isolation.$field)) { throw "Fixture isolation missing $field." }
    }
    if ($isolation.provisionedBy -notin @('GeneXus', 'XPZ')) { throw 'Fixture must be provisioned through GeneXus or verified XPZ import.' }
    $verifiedAt = [datetimeoffset]::MinValue
    $parseStyles = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [datetimeoffset]::TryParse([string]$isolation.verifiedAt, [Globalization.CultureInfo]::InvariantCulture, $parseStyles, [ref]$verifiedAt) -or
        $verifiedAt -gt [datetimeoffset]::UtcNow.AddMinutes(1)) {
        throw 'Fixture verification timestamp is invalid.'
    }

    # A fixture revision is only comparable while the on-disk provenance files
    # are unchanged. GeneXus may rewrite the .gxw metadata during a normal open
    # (for example, when it refreshes the friendly SDK version), so a manifest
    # that carries hashes must fail closed instead of silently mixing runs from
    # different KB revisions.
    $provenance = $Fixture.provenance
    if ($null -ne $provenance) {
        foreach ($field in @('gxwSha256', 'connectionSha256')) {
            if ([string]::IsNullOrWhiteSpace($provenance.$field)) {
                throw "Fixture provenance missing $field."
            }
        }
        $gxwFiles = @(Get-ChildItem -LiteralPath $ResolvedKbPath -Filter '*.gxw' -File -ErrorAction Stop)
        if ($gxwFiles.Count -ne 1) {
            throw "Fixture must contain exactly one .gxw workspace file for provenance validation; found $($gxwFiles.Count)."
        }
        $connectionFile = Join-Path $ResolvedKbPath 'knowledgebase.connection'
        if (-not (Test-Path -LiteralPath $connectionFile -PathType Leaf)) {
            throw 'Fixture knowledgebase.connection is required for provenance validation.'
        }
        $actualGxw = Get-LiveFixtureHash $gxwFiles[0].FullName -NormalizeGxw
        $actualConnection = Get-LiveFixtureHash $connectionFile
        if ($actualGxw -ine [string]$provenance.gxwSha256) {
            throw "Fixture .gxw hash does not match manifest provenance (expected $($provenance.gxwSha256), actual $actualGxw)."
        }
        if ($actualConnection -ine [string]$provenance.connectionSha256) {
            throw "Fixture connection hash does not match manifest provenance (expected $($provenance.connectionSha256), actual $actualConnection)."
        }
    }
}
