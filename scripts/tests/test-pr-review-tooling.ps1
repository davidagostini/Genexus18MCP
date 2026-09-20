$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$worktreeScript = Join-Path $root 'scripts\pr-review-worktrees.ps1'
$ripwireScript = Join-Path $root 'scripts\ripwire-review.ps1'
$mergeScript = Join-Path $root 'scripts\merge-unreleased-changelog.ps1'

foreach ($script in @($worktreeScript, $ripwireScript, $mergeScript)) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "$script has PowerShell parse errors: $($errors -join '; ')" }
}

$worktreeSource = Get-Content -LiteralPath $worktreeScript -Raw
foreach ($required in @(
    'gxmcp-pr-review-worktrees/1', 'worktree', 'add', '--detach', 'headRefOid',
    'status', '--porcelain=v1', 'Action', 'Prepare', 'Verify', 'Cleanup',
    'remoteHead', 'published', 'dirty; use -Force'
)) {
    if ($worktreeSource -notmatch [regex]::Escape($required)) {
        throw "Review worktree helper lost required guard: $required"
    }
}

$ripwireSource = Get-Content -LiteralPath $ripwireScript -Raw
foreach ($required in @('pr-context', 'quality-delta', 'token-budget', 'qualityScopes', 'OutputDirectory', 'RequireRipwire')) {
    if ($ripwireSource -notmatch [regex]::Escape($required)) {
        throw "Scoped ripwire helper lost required behavior: $required"
    }
}

$temp = Join-Path $env:TEMP ('gxmcp-pr-review-tooling-' + [guid]::NewGuid().ToString('N'))
$ours = Join-Path $temp 'ours.md'
$theirs = Join-Path $temp 'theirs.md'
$output = Join-Path $temp 'CHANGELOG.md'
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $newline = [Environment]::NewLine
    [IO.File]::WriteAllText($ours, (@(
        '# Changelog', '', '## Unreleased', '', '### Added', '', '- ours feature', '', '### Fixed', '', '- shared fix', '', '## v1.0.0', '', '- old release'
    ) -join $newline) + $newline, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($theirs, (@(
        '# Changelog', '', '## Unreleased', '', '### Added', '', '- theirs feature', '', '### Fixed', '', '- shared fix', '', '- theirs fix', '', '## v1.0.0', '', '- old release'
    ) -join $newline) + $newline, [Text.UTF8Encoding]::new($false))

    & pwsh -NoProfile -File $mergeScript -OursPath $ours -TheirsPath $theirs -OutputPath $output -Force
    if ($LASTEXITCODE -ne 0) { throw "Changelog merge helper failed with exit code $LASTEXITCODE." }
    $merged = Get-Content -LiteralPath $output -Raw
    foreach ($entry in @('- ours feature', '- theirs feature', '- shared fix', '- theirs fix')) {
        if (($merged -split [regex]::Escape($entry)).Count -ne 2) {
            throw "Merged changelog did not contain '$entry' exactly once."
        }
    }
    if (($merged -split '## v1\.0\.0').Count -ne 2) { throw 'The existing release body was not preserved.' }
    if ($merged -match '(?m)^(<<<<<<<|=======|>>>>>>>)') { throw 'Merged changelog contains conflict markers.' }

    Write-Host 'PR review tooling: parser, worktree guards, scoped ripwire contract and changelog merge passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
