$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts/check-upstream-drift.ps1'
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) { throw "PowerShell syntax errors: $($errors -join '; ')" }

$output = & pwsh -NoProfile -File $script -BaseRef 'HEAD'
if ($LASTEXITCODE -ne 0) { throw "Self-reference check failed: $($output -join ' ')" }
$result = ($output -join "`n") | ConvertFrom-Json
if ($result.status -ne 'available' -or $result.behind -ne 0 -or $result.ahead -ne 0) {
    throw 'HEAD self-reference must report zero drift.'
}
if ([string]::IsNullOrWhiteSpace($result.repository) -or [string]::IsNullOrWhiteSpace($result.headCommit)) {
    throw 'Drift report must include repository and head commit.'
}
Write-Host 'PASS: upstream drift report is parseable and self-reference is zero.'
