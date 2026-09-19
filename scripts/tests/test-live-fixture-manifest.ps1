$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\live-fixture.ps1')

$fixtureRoot = Join-Path $env:TEMP ('gxmcp-fixture-generator-' + [guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $fixtureRoot 'generated.fixture.json'
$generator = Join-Path $root 'scripts\new-live-fixture-manifest.ps1'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
try {
    $gxwPath = Join-Path $fixtureRoot 'Synthetic.gxw'
    $connectionPath = Join-Path $fixtureRoot 'knowledgebase.connection'
    Set-Content -LiteralPath $gxwPath -Value '<KnowledgeBase><VersionNumber>test</VersionNumber><Name>Synthetic</Name></KnowledgeBase>' -Encoding utf8
    Set-Content -LiteralPath $connectionPath -Value 'server=(localdb);database=synthetic' -Encoding utf8

    & pwsh -NoProfile -File $generator `
        -KbPath $fixtureRoot `
        -FixtureId 'synthetic-generator' `
        -FixtureRevision 'revision-1' `
        -Generator 'GeneXus18-net' `
        -KbDatabaseId 'isolated-kb' `
        -ApplicationDatabaseId 'isolated-app' `
        -Evidence 'script-test' `
        -ProvisionedBy GeneXus `
        -ConfirmIsolated `
        -OutputPath $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "Manifest generator failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Manifest generator did not write the requested output.' }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.fixtureId -ne 'synthetic-generator' -or
        $manifest.kbPath -ine (Resolve-Path -LiteralPath $fixtureRoot).Path -or
        $manifest.synthetic -ne $true -or $manifest.disposable -ne $true -or
        $manifest.isolation.verified -ne $true) {
        throw 'Generated manifest did not preserve the required identity and isolation contract.'
    }
    if ($manifest.provenance.gxwSha256 -ine (Get-LiveFixtureHash $gxwPath -NormalizeGxw) -or
        $manifest.provenance.connectionSha256 -ine (Get-LiveFixtureHash $connectionPath)) {
        throw 'Generated provenance hashes do not match the source files.'
    }
    Assert-LiveFixture $manifest (Resolve-Path -LiteralPath $fixtureRoot).Path

    $existingOutput = @(& pwsh -NoProfile -File $generator `
        -KbPath $fixtureRoot -FixtureId 'synthetic-generator' -FixtureRevision 'revision-2' `
        -Generator 'GeneXus18-net' -KbDatabaseId 'isolated-kb' -ApplicationDatabaseId 'isolated-app' `
        -Evidence 'script-test' -ProvisionedBy GeneXus -ConfirmIsolated -OutputPath $manifestPath 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($existingOutput -join "`n") -notmatch 'already exists') {
        throw 'Manifest generator must refuse an existing output without -Force.'
    }

    Set-Content -LiteralPath $gxwPath -Value '<changed />' -Encoding utf8
    $changedFailed = $false
    try { Assert-LiveFixture $manifest (Resolve-Path -LiteralPath $fixtureRoot).Path } catch { $changedFailed = $true }
    if (-not $changedFailed) { throw 'Fixture validation must reject a changed provenance file.' }

    $missingConfirmation = @(& pwsh -NoProfile -File $generator `
        -KbPath $fixtureRoot -FixtureId 'missing-confirmation' -FixtureRevision 'revision-1' `
        -Generator 'GeneXus18-net' -KbDatabaseId 'isolated-kb' -ApplicationDatabaseId 'isolated-app' `
        -Evidence 'script-test' -ProvisionedBy GeneXus -ConfirmIsolated:$false 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($missingConfirmation -join "`n") -notmatch 'ConfirmIsolated') {
        throw 'Manifest generator must fail closed without the explicit isolation confirmation.'
    }
    Write-Host 'PASS: live fixture generator, overwrite guard, provenance validation and isolation confirmation.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
