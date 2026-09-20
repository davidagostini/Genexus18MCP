$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$drySummary = Join-Path $env:TEMP ('gxmcp-preflight-test-' + [guid]::NewGuid().ToString('N') + '.json')
$matrixDrySummary = Join-Path $env:TEMP ('gxmcp-preflight-matrix-test-' + [guid]::NewGuid().ToString('N') + '.json')
$requiredSummary = Join-Path $env:TEMP ('gxmcp-preflight-required-' + [guid]::NewGuid().ToString('N') + '.json')
$localSummary = Join-Path $env:TEMP ('gxmcp-preflight-local-' + [guid]::NewGuid().ToString('N') + '.json')
$fixtureRoot = Join-Path $env:TEMP ('gxmcp-preflight-fixtures-' + [guid]::NewGuid().ToString('N'))
$SummaryPath = $null
$requiredNames = @(
    'release metadata parity', 'tool contract validation', 'operation contract inventory',
    'v3 plan readiness', 'Python script tests', 'PowerShell script tests',
    'CLI tests', 'CLI lint', 'Nexus IDE checks', 'solution build and tests',
    'Release warning baseline',
    'live KB gate'
)
try {
    $preflightPath = Join-Path $root 'scripts/release-preflight.ps1'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($preflightPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $fixtureFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-LocalLiveKbPath' }, $true)
    if (-not $fixtureFunction) { throw 'Missing Get-LocalLiveKbPath production function.' }
    . ([scriptblock]::Create($fixtureFunction.Extent.Text))
    $catalog = [pscustomobject]@{
        primaryMajor = '18'
        supportedMajors = @(
            [pscustomobject]@{ major = '17'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' }
            [pscustomobject]@{ major = '18'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus18' }
        )
    }
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'KBTeste'), (Join-Path $fixtureRoot 'KBTeste17') -Force | Out-Null
    $auto18 = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus18' -KbRoot $fixtureRoot)
    $auto17 = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' -KbRoot $fixtureRoot)
    $auto17Custom = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'D:\SDK\GeneXus17Trial' -KbRoot $fixtureRoot)
    $none = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'C:\SDK\Unknown' -KbRoot (Join-Path $env:TEMP 'gxmcp-no-fixtures'))
    if ($auto18.Count -ne 1 -or $auto18[0].major -ne '18' -or $auto18[0].path -ne (Join-Path $fixtureRoot 'KBTeste')) { throw 'GeneXus 18 fixture autodetection selected the wrong KB.' }
    if ($auto17.Count -ne 1 -or $auto17[0].major -ne '17' -or $auto17[0].path -ne (Join-Path $fixtureRoot 'KBTeste17')) { throw 'GeneXus 17 fixture autodetection selected the wrong KB.' }
    if ($auto17Custom.Count -ne 1 -or $auto17Custom[0].major -ne '17' -or $auto17Custom[0].path -ne (Join-Path $fixtureRoot 'KBTeste17')) { throw 'Custom GeneXus 17 fixture autodetection selected the wrong KB.' }
    if ($none.Count -ne 1 -or $null -ne $none[0]) { throw 'Fixture autodetection returned a KB that does not exist.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -SkipLive -SummaryPath $drySummary
    if ($LASTEXITCODE -ne 0) { throw "Dry-run preflight failed with exit code $LASTEXITCODE." }
    $summary = Get-Content -LiteralPath $drySummary -Raw | ConvertFrom-Json
    if ($summary.schemaVersion -ne 'gxmcp-release-preflight/1') { throw 'Unexpected preflight summary schema.' }
    if ($summary.executionMode -ne 'dry-run' -or $summary.wallDurationSeconds -lt 0 -or $summary.phaseDurationTotalSeconds -lt 0) {
        throw 'Preflight summary must expose nonnegative wall and phase timing metrics.'
    }
    $actualNames = @($summary.phases | ForEach-Object name)
    if (($actualNames -join '|') -ne ($requiredNames -join '|')) { throw "Preflight phase order changed: $($actualNames -join ', ')" }
    $buildIndex = [array]::IndexOf($actualNames, 'solution build and tests')
    foreach ($staticName in @('tool contract validation', 'operation contract inventory', 'v3 plan readiness', 'Python script tests', 'PowerShell script tests')) {
        if ([array]::IndexOf($actualNames, $staticName) -ge $buildIndex) {
            throw "Cheap static phase '$staticName' must run before the solution build and tests."
        }
    }
    if (@($summary.phases | Where-Object status -eq 'dry-run').Count -ne 11) { throw 'All non-live phases must be marked dry-run.' }
    if (@($summary.phases | Where-Object status -eq 'skipped').Count -ne 1) { throw 'Live skip must be explicit in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -SkipLive -LiveMajors '17,18' -LiveGxPathMap '17=C:\SDK\GX17' -SummaryPath $matrixDrySummary *> $null
    if ($LASTEXITCODE -ne 0) { throw "Matrix dry-run preflight failed with exit code $LASTEXITCODE." }
    $matrixSummary = Get-Content -LiteralPath $matrixDrySummary -Raw | ConvertFrom-Json
    if ($matrixSummary.liveMode -ne 'matrix') { throw 'Matrix options were not reflected in the release preflight summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -RequireBuildAll -LiveKbPath (Join-Path $fixtureRoot 'missing') -SummaryPath $requiredSummary *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Required live Build All gate must fail closed when no fixture is configured.' }
    $required = Get-Content -LiteralPath $requiredSummary -Raw | ConvertFrom-Json
    $requiredLive = @($required.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    if ($required.status -ne 'failed' -or $requiredLive.status -ne 'skipped') { throw 'Required live gate failure was not recorded in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -RequireBuildAll -LiveKbPath (Join-Path $fixtureRoot 'KBTeste') -SummaryPath $localSummary *> $null
    if ($LASTEXITCODE -ne 0) { throw 'A local KB must be sufficient for the dry-run live gate without a fixture manifest.' }
    $local = Get-Content -LiteralPath $localSummary -Raw | ConvertFrom-Json
    $localLive = @($local.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    if ($localLive.status -ne 'dry-run' -or $local.liveKbPath -ne (Join-Path $fixtureRoot 'KBTeste')) { throw 'Local KB live source was not recorded in the preflight summary.' }

    $releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
    if ($releaseSource -match "'-SkipLive'") { throw 'Canonical release entrypoint must allow configured live preflight values to participate.' }

    $preflightSource = Get-Content -LiteralPath (Join-Path $root 'scripts/release-preflight.ps1') -Raw
    foreach ($marker in @(
        'LiveMajors', 'LiveGxPathMap', 'test-live-matrix.ps1', "liveMode =",
        'Start-PreflightPhase', 'Complete-PreflightPhase', 'Invoke-PreflightParallel',
        'ResumeSummaryPath', 'artifactFingerprint', 'sourceCommit', 'executionMode'
    )) {
        if ($preflightSource -notmatch [regex]::Escape($marker)) { throw "Release preflight is missing multi-version live marker: $marker" }
    }
    if ($preflightSource.Contains(', $liveDefinition')) { throw 'Live KB gate must not run in parallel with the solution MSBuild phase.' }
    $parallelCall = $preflightSource.IndexOf('Invoke-PreflightParallel -Definitions', [StringComparison]::Ordinal)
    $liveCall = $preflightSource.IndexOf('Invoke-PreflightPhase @liveDefinition', [StringComparison]::Ordinal)
    if ($parallelCall -lt 0 -or $liveCall -le $parallelCall) { throw 'Live KB gate must run after the shared solution test wave.' }

    # Exercise the new parallel coordinator in dry-run mode. This keeps the
    # test independent of SDK availability while proving deterministic result
    # ordering and one result per launched phase.
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/release-preflight.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $functionNames = @(
        'Format-PreflightCommand', 'Write-PreflightSummary',
        'Get-PreflightArtifactFingerprint', 'Test-PreflightResumeInputs', 'Get-ReusablePreflightPhase', 'New-PreflightPhaseState',
        'Start-PreflightPhase', 'Complete-PreflightPhase',
        'Add-PreflightPhaseResult', 'Invoke-PreflightParallel'
    )
    foreach ($name in $functionNames) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing parallel preflight function: $name" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $summary = [ordered]@{
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        phases = New-Object System.Collections.Generic.List[object]
        status = 'running'
    }
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-preflight-parallel-' + [guid]::NewGuid().ToString('N') + '.json')
    $DryRun = $true
    $PhaseTimeoutSeconds = 30
    $resumeEnabled = $false
    $resumePhases = @{}
    $resumeReason = $null
    # Starting a phase must persist its running state before completion so an
    # interrupted host leaves a resumable incremental summary.
    $DryRun = $false
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-preflight-running-' + [guid]::NewGuid().ToString('N') + '.json')
    $runningState = Start-PreflightPhase -Name 'incremental phase' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '0') -WorkingDirectory $root
    $runningSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
    $runningPhases = @($runningSummary.phases | ForEach-Object { $_ })
    if ($runningPhases.Count -ne 1 -or $runningPhases[0].name -ne 'incremental phase' -or $runningPhases[0].status -ne 'running') {
        throw 'Preflight must persist a running phase before waiting for completion.'
    }
    $runningResult = Complete-PreflightPhase -State $runningState
    if ($runningResult.status -ne 'passed') { throw 'Incremental phase did not complete successfully.' }
    Add-PreflightPhaseResult -Phase $runningResult
    $completedSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
    $completedPhases = @($completedSummary.phases | ForEach-Object { $_ })
    if ($completedPhases.Count -ne 1 -or $completedPhases[0].status -ne 'passed') {
        throw 'Completing a phase must update its existing summary entry without duplication.'
    }
    $DryRun = $true
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-preflight-parallel-' + [guid]::NewGuid().ToString('N') + '.json')
    $summary.phases.Clear()
    $artifactRoot = Join-Path $fixtureRoot 'artifact'
    New-Item -ItemType Directory -Path (Join-Path $artifactRoot 'publish/worker') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/GxMcp.Gateway.exe') -Value 'gateway' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/worker/GxMcp.Worker.exe') -Value 'worker' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/tool_definitions.json') -Value '{}' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/nexus-ide.vsix') -Value 'vsix' -NoNewline
    $fingerprint = @(Get-PreflightArtifactFingerprint -Root $artifactRoot)
    if ($fingerprint.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$fingerprint[0])) {
        throw 'Artifact fingerprint must be one scalar value when all release artifacts exist.'
    }
    $Version = '3.5.0'
    $GxPath = 'sdk-path'
    $LiveKbPath = 'kb-path'
    $liveMode = 'single'
    $LiveMajors = @()
    $LiveGxPathMap = @()
    $sourceCommit = 'source-commit'
    $artifactFingerprint = [string]$fingerprint[0]
    $matchingResume = [pscustomobject]@{
        schemaVersion = 'gxmcp-release-preflight/1'; status = 'failed'; root = $root
        version = '3.5.0'; sourceCommit = 'source-commit'; gxPath = 'sdk-path'
        liveKbPath = 'kb-path'; liveMode = 'single'; liveMajors = @(); liveGxPathMap = @()
        artifactFingerprint = $artifactFingerprint
    }
    if (-not (Test-PreflightResumeInputs -PriorSummary $matchingResume)) {
        throw 'A matching preflight summary must be resumable.'
    }
    $matchingResume.sourceCommit = 'different-commit'
    if (Test-PreflightResumeInputs -PriorSummary $matchingResume) {
        throw 'A changed source commit must invalidate preflight resume.'
    }
    $matchingResume.sourceCommit = $sourceCommit
    $matchingResume.artifactFingerprint = 'different-artifact'
    if (Test-PreflightResumeInputs -PriorSummary $matchingResume) {
        throw 'A changed artifact fingerprint must invalidate preflight resume.'
    }
    $artifactFingerprint = $null
    $matchingResume.artifactFingerprint = $null
    if (Test-PreflightResumeInputs -PriorSummary $matchingResume) {
        throw 'A missing artifact fingerprint must invalidate preflight resume.'
    }
    $artifactFingerprint = [string]$fingerprint[0]
    $matchingResume.artifactFingerprint = $artifactFingerprint
    $definitions = @(
        [ordered]@{ Name = 'parallel first'; Executable = 'cmd.exe'; Arguments = @('/c', 'exit', '0'); WorkingDirectory = $root },
        [ordered]@{ Name = 'parallel second'; Executable = 'cmd.exe'; Arguments = @('/c', 'exit', '0'); WorkingDirectory = $root }
    )
    $parallelResults = @(Invoke-PreflightParallel -Definitions $definitions)
    if ($parallelResults.Count -ne 2 -or $summary.phases.Count -ne 2) {
        throw 'Parallel preflight must return and record every phase.'
    }
    if (($parallelResults.name -join '|') -ne 'parallel first|parallel second' -or
        @($summary.phases | ForEach-Object status) -contains 'failed' -or
        @($summary.phases | ForEach-Object status) -contains 'running') {
        throw 'Parallel preflight must preserve definition order and terminal dry-run statuses.'
    }
    if (-not (@($summary.phases | ForEach-Object status) -contains 'dry-run')) {
        throw 'Parallel dry-run phases must be marked dry-run.'
    }

    # A passed phase from a matching prior summary must be reusable without
    # starting a child process; the record remains observable as reused.
    $resumeEnabled = $true
    $resumeSummaryPath = 'fixture-summary.json'
    $resumePhases = @{
        'reusable phase' = [pscustomobject]@{ status = 'passed'; command = 'cmd.exe /c exit 0'; exitCode = 0; durationSeconds = 1.25 }
    }
    $reusableState = Start-PreflightPhase -Name 'reusable phase' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '0') -WorkingDirectory $root
    $reusableResult = Complete-PreflightPhase -State $reusableState
    if ($reusableResult.status -ne 'passed' -or -not $reusableResult.reused) {
        throw 'Matching passed preflight phases must be marked reusable.'
    }

    # Load the production runner and exercise a nonzero command without
    # starting the real release matrix. AllowFailure keeps the phase object so
    # the test can inspect exit-code propagation.
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts\release-preflight.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    foreach ($name in @('Format-PreflightCommand', 'Write-PreflightSummary', 'Invoke-PreflightPhase')) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing $name production function." }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $summary = [ordered]@{
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        phases = New-Object System.Collections.Generic.List[object]
        endedAtUtc = $null
    }
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-preflight-phase-' + [guid]::NewGuid().ToString('N') + '.json')
    $DryRun = $false
    $phase = Invoke-PreflightPhase -Name 'mock failure' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '7') -AllowFailure
    if ($phase.exitCode -ne 7 -or $phase.status -ne 'failed') { throw 'Nonzero phase exit code was not preserved.' }
    if (-not (Test-Path -LiteralPath $SummaryPath)) { throw 'Phase summary was not written atomically.' }

    Write-Host 'release-preflight: order, summary shape, skip and exit propagation passed' -ForegroundColor Green
}
finally {
    foreach ($path in @($drySummary, $matrixDrySummary, $requiredSummary, $localSummary, $SummaryPath, $fixtureRoot)) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        if ($path -eq $fixtureRoot) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}
