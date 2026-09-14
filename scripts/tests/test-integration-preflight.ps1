$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sourcePath = Join-Path $root 'scripts\integration-preflight.ps1'
$source = Get-Content -LiteralPath $sourcePath -Raw

foreach ($requiredText in @(
    'gxmcp-integration-preflight/1',
    'Get-ChangedPaths',
    "'diff', '--cached', '--name-only'",
    "'ls-files', '--others', '--exclude-standard'",
    'Conflict markers remain',
    'tool_definitions',
    'tools-list.response.json',
    'CHANGELOG.md',
    'ReadToEndAsync',
    'Kill($true)',
    'Get-Command $resolvedExecutable',
    'resolvedExecutable',
    'TimeoutSeconds',
    '--no-restore',
    "'-m:1'",
    'validate-tool-contracts.py',
    'run-release-script-tests.ps1',
    'npm.cmd',
    '$ValidateOnly'
)) {
    if ($source -notmatch [regex]::Escape($requiredText)) {
        throw "Integration preflight lost required guard: $requiredText"
    }
}

$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $sourcePath,
    [ref]$null,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw "Integration preflight has PowerShell parse errors: $($parseErrors -join '; ')"
}

Write-Host 'integration-preflight contract: PASS'
