[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main'
)

$ErrorActionPreference = 'Stop'

function Invoke-Git([string[]]$Arguments) {
    $result = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($result -join ' ')"
    }
    return ($result -join "`n").Trim()
}

$root = Invoke-Git @('rev-parse', '--show-toplevel')
$resolvedBase = Invoke-Git @('rev-parse', '--verify', "$BaseRef`^{commit}")
$head = Invoke-Git @('rev-parse', 'HEAD')
$count = Invoke-Git @('rev-list', '--left-right', '--count', "$BaseRef...HEAD")
$parts = $count -split '\s+'
if ($parts.Count -ne 2) {
    throw "Unexpected git rev-list count: '$count'"
}

[pscustomobject]@{
    status = 'available'
    repository = $root
    baseRef = $BaseRef
    baseCommit = $resolvedBase
    headCommit = $head
    behind = [int]$parts[0]
    ahead = [int]$parts[1]
    action = if ([int]$parts[0] -gt 0) {
        "Review $BaseRef before diagnosing an issue; it contains commits not present in HEAD."
    } elseif ([int]$parts[1] -gt 0) {
        "HEAD contains commits beyond $BaseRef; inspect the issue history and diff before porting a fix."
    } else {
        "HEAD and $BaseRef point to the same history tip."
    }
} | ConvertTo-Json -Compress
