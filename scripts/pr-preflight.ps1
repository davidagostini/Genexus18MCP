[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [int]$PullRequest = 0,
    [switch]$RequireRipwire
)

$ErrorActionPreference = 'Stop'

function Fail-Preflight([string]$Message) {
    Write-Error "PR preflight failed: $Message"
    exit 1
}

if ($PullRequest -le 0) {
    Fail-Preflight "Missing required -PullRequest <number> argument. Usage: pwsh -File scripts/pr-preflight.ps1 <PR_NUMBER>"
}

function Get-GhJson([string[]]$Arguments, [string]$GhPath = 'gh') {
    $output = @(& $GhPath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Fail-Preflight "gh $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    $text = $output -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) {
        Fail-Preflight "gh $($Arguments -join ' ') returned empty output."
    }
    try {
        $value = $text | ConvertFrom-Json
    } catch {
        Fail-Preflight "gh $($Arguments -join ' ') returned invalid JSON."
    }
    if ($null -eq $value) {
        Fail-Preflight "gh $($Arguments -join ' ') returned a null JSON value."
    }
    return $value
}

function Get-RipwireChangedPathCount([string]$BaseRef, [string]$RepositoryPath = '.') {
    $paths = New-Object System.Collections.Generic.List[string]
    $baseDiff = @(& git -C $RepositoryPath diff --name-only "$BaseRef...HEAD" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not calculate the diff against ripwire base '$BaseRef'."
    }
    foreach ($path in $baseDiff) {
        if (-not [string]::IsNullOrWhiteSpace([string]$path)) { [void]$paths.Add(([string]$path).Trim()) }
    }
    foreach ($arguments in @(
        @('diff', '--name-only'),
        @('diff', '--cached', '--name-only'),
        @('ls-files', '--others', '--exclude-standard')
    )) {
        $localDiff = @(& git -C $RepositoryPath @arguments 2>$null)
        if ($LASTEXITCODE -ne 0) { throw "Could not calculate the local Git diff for ripwire validation." }
        foreach ($path in $localDiff) {
            if (-not [string]::IsNullOrWhiteSpace([string]$path)) { [void]$paths.Add(([string]$path).Trim()) }
        }
    }
    return @($paths | Select-Object -Unique).Count
}

function Invoke-RipwireGate([string]$BaseRef, [switch]$Require, [string]$RipwirePath, [string]$RepositoryPath = '.') {
    if ([string]::IsNullOrWhiteSpace($RipwirePath)) {
        $command = Get-Command ripwire -ErrorAction SilentlyContinue
        if ($command) { $RipwirePath = $command.Source }
    } elseif (-not (Test-Path -LiteralPath $RipwirePath)) {
        $RipwirePath = $null
    }

    if ([string]::IsNullOrWhiteSpace($RipwirePath)) {
        if ($Require) {
            return [pscustomobject]@{ status = 'failed'; exitCode = 127; reason = 'ripwire is not found on PATH.' }
        }
        return [pscustomobject]@{ status = 'skipped'; exitCode = 0; reason = 'ripwire is not found on PATH.' }
    }

    $output = @()
    try {
        $output = @(& $RipwirePath $RepositoryPath "--pr-context=$BaseRef" 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    } catch {
        $exitCode = 1
        $output = @($_.Exception.Message)
    }
    foreach ($line in $output) { Write-Host "    $line" }
    if ($exitCode -ne 0) {
        return [pscustomobject]@{ status = 'failed'; exitCode = $exitCode; reason = "ripwire exited with code $exitCode." }
    }

    $text = $output -join "`n"
    if ($text -match '(?i)(cannot materialize|could not materialize|cannot find the path|no git HEAD|could not parse the tree)') {
        return [pscustomobject]@{ status = 'failed'; exitCode = 65; reason = 'ripwire returned a tree-materialization or parsing error despite exit code 0.' }
    }
    if ($text -notmatch '(?m)<pr-context\b') {
        return [pscustomobject]@{ status = 'failed'; exitCode = 65; reason = 'ripwire returned no <pr-context> bundle.' }
    }
    $contextMatch = [regex]::Match($text, '<pr-context\b[^>]*\bfiles="(?<files>\d+)"')
    if (-not $contextMatch.Success) {
        return [pscustomobject]@{ status = 'failed'; exitCode = 65; reason = 'ripwire <pr-context> bundle did not expose its changed-file count.' }
    }
    try {
        $changedCount = Get-RipwireChangedPathCount -BaseRef $BaseRef -RepositoryPath $RepositoryPath
    } catch {
        return [pscustomobject]@{ status = 'failed'; exitCode = 65; reason = $_.Exception.Message }
    }
    $reportedCount = [int]$contextMatch.Groups['files'].Value
    if ($changedCount -gt 0 -and $reportedCount -eq 0) {
        return [pscustomobject]@{ status = 'failed'; exitCode = 65; reason = "ripwire reported files=0 while Git exposes $changedCount changed path(s)." }
    }
    return [pscustomobject]@{ status = 'passed'; exitCode = 0; reason = $null }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail-Preflight "GitHub CLI (gh) is required."
}

$pr = Get-GhJson @(
    'pr', 'view', $PullRequest.ToString(),
    '--json', 'number,state,isDraft,mergeStateStatus,reviewDecision,headRefName,headRefOid,headRepository,baseRefName,url'
)

if ($pr.state -ne 'OPEN') {
    Fail-Preflight "PR #$PullRequest must be OPEN; current state is '$($pr.state)'."
}
if ($pr.isDraft -eq $true) {
    Fail-Preflight "PR #$PullRequest is still a draft."
}
if ($pr.mergeStateStatus -ne 'CLEAN') {
    Fail-Preflight "PR #$PullRequest is not cleanly mergeable; mergeStateStatus='$($pr.mergeStateStatus)'."
}
if ($pr.reviewDecision -ne 'APPROVED') {
    Fail-Preflight "PR #$PullRequest requires an approved review; reviewDecision='$($pr.reviewDecision)'."
}

$headRepo = $pr.headRepository.nameWithOwner
if ([string]::IsNullOrWhiteSpace($headRepo)) {
    Fail-Preflight "PR #$PullRequest did not expose the head repository."
}
if ([string]::IsNullOrWhiteSpace($pr.headRefName) -or [string]::IsNullOrWhiteSpace($pr.headRefOid)) {
    Fail-Preflight "PR #$PullRequest did not expose a complete head ref and OID."
}

$repo = Get-GhJson @('repo', 'view', '--json', 'nameWithOwner')
if ([string]::IsNullOrWhiteSpace($pr.url)) {
    Fail-Preflight "PR #$PullRequest did not expose its URL, so the base repository could not be verified."
}
$prUri = [Uri]$pr.url
$pathParts = @($prUri.AbsolutePath.Trim('/').Split('/'))
if ($pathParts.Count -lt 2) {
    Fail-Preflight "PR #$PullRequest URL does not contain a repository path: $($pr.url)"
}
$baseRepo = "$($pathParts[0])/$($pathParts[1])"
if ($repo.nameWithOwner -ne $baseRepo) {
    Fail-Preflight "Current checkout is '$($repo.nameWithOwner)', but PR base is '$baseRepo'."
}

$checksOutput = @(& gh pr checks $PullRequest --json name,state,bucket,link 2>&1)
if ($LASTEXITCODE -ne 0) {
    Fail-Preflight "gh pr checks failed: $($checksOutput -join ' ')"
}
try {
    $checks = @($checksOutput -join "`n" | ConvertFrom-Json)
} catch {
    Fail-Preflight "gh pr checks returned invalid JSON."
}
if ($checks.Count -eq 0) {
    Fail-Preflight "PR #$PullRequest has no reported checks."
}

$notPassing = @($checks | Where-Object {
    $_.bucket -notin @('pass', 'skipping') -and $_.state -notin @('SUCCESS', 'SKIPPED')
})
if ($notPassing.Count -gt 0) {
    $names = ($notPassing | ForEach-Object { "$($_.name) [$($_.state)]" }) -join ', '
    Fail-Preflight "PR #$PullRequest has non-passing checks: $names"
}

$baseRef = if ($pr.baseRefName) { "origin/$($pr.baseRefName)" } else { "origin/main" }
$ripwireResult = Invoke-RipwireGate -BaseRef $baseRef -Require:$RequireRipwire
if ($ripwireResult.status -eq 'passed') {
    Write-Host "Running ripwire architectural blast radius analysis..."
    Write-Host "  ripwire: blast radius and caller analysis passed." -ForegroundColor Green
} elseif ($ripwireResult.status -eq 'failed') {
    Fail-Preflight $ripwireResult.reason
} else {
    Write-Warning "ripwire architectural blast radius analysis skipped: $($ripwireResult.reason)"
}

if ($ripwireResult.status -eq 'skipped') {
    Write-Host "PR #$PullRequest is ready for merge (mandatory gates passed; ripwire analysis skipped)." -ForegroundColor Yellow
} else {
    Write-Host "PR #$PullRequest is ready for merge." -ForegroundColor Green
}
Write-Host "  base: $baseRepo/$($pr.baseRefName)"
Write-Host "  head: $headRepo/$($pr.headRefName) @ $($pr.headRefOid)"
Write-Host "  review: APPROVED"
Write-Host "  checks: $($checks.Count) reported, all passing"
