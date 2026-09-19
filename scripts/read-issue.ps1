[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$Issue,
    [string]$GhPath = 'gh'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseHelper = Join-Path $PSScriptRoot 'release-issues.ps1'
if (-not (Test-Path -LiteralPath $releaseHelper -PathType Leaf)) {
    throw "Release issue helper is missing: $releaseHelper"
}

$issueNumber = $Issue
. $releaseHelper -DefineOnly
$record = Get-ReleaseIssueData -IssueNumber $issueNumber -GhPath $GhPath
$record | ConvertTo-Json -Depth 20
