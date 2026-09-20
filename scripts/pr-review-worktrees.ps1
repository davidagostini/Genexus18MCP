[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string[]]$PullRequest = @(),

    [ValidateSet('Prepare', 'Verify', 'Cleanup')]
    [string]$Action = 'Prepare',

    [string]$Root,
    [string]$ManifestPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $output = @(& git -C $WorkingDirectory @Arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$WorkingDirectory' $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    return $output
}

function Invoke-GhJson {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& gh @Arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    $text = $output -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "gh $($Arguments -join ' ') returned empty output."
    }
    try {
        return $text | ConvertFrom-Json
    } catch {
        throw "gh $($Arguments -join ' ') returned invalid JSON."
    }
}

function Get-CurrentPullRequest {
    param([Parameter(Mandatory = $true)][int]$Number)

    $pr = Invoke-GhJson @(
        'pr', 'view', $Number.ToString(),
        '--json', 'number,state,headRefName,headRefOid,headRepository,baseRefName,baseRefOid,url'
    )
    if ($pr.state -ne 'OPEN') {
        throw "PR #$Number must be OPEN; current state is '$($pr.state)'."
    }
    if ([string]::IsNullOrWhiteSpace([string]$pr.headRefName) -or
        [string]::IsNullOrWhiteSpace([string]$pr.headRefOid) -or
        [string]::IsNullOrWhiteSpace([string]$pr.headRepository.nameWithOwner)) {
        throw "PR #$Number did not expose a complete head repository/ref/OID."
    }
    return $pr
}

function Get-RepositoryRoot {
    $output = @(& git rev-parse --show-toplevel 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) {
        throw "The current directory is not inside a Git repository."
    }
    return [IO.Path]::GetFullPath($output[0].Trim())
}

function Write-Manifest {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText(
        $temporary,
        (($Manifest | ConvertTo-Json -Depth 12) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Read-Manifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Review manifest '$Path' was not found."
    }
    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "Review manifest '$Path' is not valid JSON."
    }
    if ($manifest.schemaVersion -ne 'gxmcp-pr-review-worktrees/1') {
        throw "Unsupported review manifest schema in '$Path'."
    }
    return $manifest
}

function Test-CleanWorktree {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }
    $status = @(& git -C $Path status --porcelain=v1 --untracked-files=all 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect review worktree '$Path'."
    }
    return $status.Count -eq 0
}

function Get-SelectedEntries {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [int[]]$Numbers
    )

    $entries = @($Manifest.entries)
    if ($Numbers.Count -eq 0) {
        return $entries
    }
    $selected = @($entries | Where-Object { $Numbers -contains [int]$_.pullRequest })
    $missing = @($Numbers | Where-Object { $selected.pullRequest -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "Review manifest has no entry for PR(s): $($missing -join ', ')."
    }
    return $selected
}

function Remove-PreparedEntry {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [switch]$AllowDirty
    )

    $path = [string]$Entry.worktree
    if (Test-Path -LiteralPath $path -PathType Container) {
        if (-not $AllowDirty -and -not (Test-CleanWorktree -Path $path)) {
            throw "Review worktree '$path' is dirty; use -Force only when discarding it is intentional."
        }
        $arguments = @('worktree', 'remove')
        if ($AllowDirty) { $arguments += '--force' }
        $arguments += $path
        [void](Invoke-GitText -Arguments $arguments -WorkingDirectory $RepositoryRoot)
    }
    $remoteRef = [string]$Entry.remoteRef
    if (-not [string]::IsNullOrWhiteSpace($remoteRef)) {
        $delete = @(& git -C $RepositoryRoot update-ref -d $remoteRef 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Could not delete local review ref '$remoteRef': $($delete -join ' ')"
        }
    }
}

$repositoryRoot = Get-RepositoryRoot
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        if ($Action -ne 'Prepare') {
            throw "-Root or -ManifestPath is required for -Action $Action."
        }
        $Root = Join-Path $env:TEMP ('gxmcp-pr-review-' + [guid]::NewGuid().ToString('N'))
    }
    $ManifestPath = Join-Path $Root 'review-manifest.json'
} else {
    $ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if ([string]::IsNullOrWhiteSpace($Root)) {
        $Root = Split-Path -Parent $ManifestPath
    }
}
$Root = [IO.Path]::GetFullPath($Root)

try {
    $normalizedPullRequests = @()
    foreach ($value in $PullRequest) {
        foreach ($part in ([string]$value -split ',')) {
            $trimmed = $part.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
            if ($trimmed -notmatch '^\d+$') {
                throw "Invalid PR number '$trimmed'. Use integers separated by commas or repeated -PullRequest values."
            }
            $normalizedPullRequests += [int]$trimmed
        }
    }
    $PullRequest = $normalizedPullRequests
    switch ($Action) {
        'Prepare' {
            if ($PullRequest.Count -eq 0) {
                throw "At least one PR number is required for -Action Prepare."
            }
            $duplicates = @($PullRequest | Group-Object | Where-Object Count -gt 1)
            if ($duplicates.Count -gt 0) {
                throw "Duplicate PR numbers are not allowed: $($duplicates.Name -join ', ')."
            }
            New-Item -ItemType Directory -Path $Root -Force | Out-Null
            if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
                throw "Review manifest '$ManifestPath' already exists; choose another -Root or clean it explicitly."
            }

            $entries = @()
            $created = @()
            try {
                foreach ($number in $PullRequest) {
                    $pr = Get-CurrentPullRequest -Number $number
                    $headRepository = [string]$pr.headRepository.nameWithOwner
                    $headRef = [string]$pr.headRefName
                    $headOid = [string]$pr.headRefOid
                    $remote = "https://github.com/$headRepository.git"
                    $remoteRef = "refs/remotes/codex-pr-review/$number"
                    $worktree = Join-Path $Root ("pr-{0}" -f $number)
                    if (Test-Path -LiteralPath $worktree) {
                        throw "Review worktree '$worktree' already exists."
                    }

                    $fetchSpec = "+refs/heads/${headRef}:$remoteRef"
                    [void](Invoke-GitText -Arguments @('fetch', '--no-tags', '--force', $remote, $fetchSpec) -WorkingDirectory $repositoryRoot)
                    $fetchedOid = [string](@(Invoke-GitText -Arguments @('rev-parse', $remoteRef) -WorkingDirectory $repositoryRoot)[0]).Trim()
                    if ($fetchedOid -ne $headOid) {
                        throw "PR #$number moved while it was being prepared: GitHub=$headOid, fetched=$fetchedOid. Re-read the PR and retry."
                    }
                    [void](Invoke-GitText -Arguments @('worktree', 'add', '--detach', $worktree, $headOid) -WorkingDirectory $repositoryRoot)
                    $created += [pscustomobject]@{ worktree = $worktree; remoteRef = $remoteRef }
                    $actualOid = [string](@(Invoke-GitText -Arguments @('rev-parse', 'HEAD') -WorkingDirectory $worktree)[0]).Trim()
                    if ($actualOid -ne $headOid -or -not (Test-CleanWorktree -Path $worktree)) {
                        throw "Prepared review worktree '$worktree' did not verify clean at the requested head."
                    }
                    $entries += [ordered]@{
                        pullRequest = $number
                        url = [string]$pr.url
                        baseRef = [string]$pr.baseRefName
                        baseOid = [string]$pr.baseRefOid
                        headRepository = $headRepository
                        headRef = $headRef
                        headOid = $headOid
                        remote = $remote
                        remoteRef = $remoteRef
                        worktree = $worktree
                    }
                }
            } catch {
                foreach ($item in $created) {
                    try { Remove-PreparedEntry -Entry $item -RepositoryRoot $repositoryRoot -AllowDirty } catch { }
                }
                throw
            }
            $manifest = [ordered]@{
                schemaVersion = 'gxmcp-pr-review-worktrees/1'
                repository = $repositoryRoot
                createdAtUtc = [DateTime]::UtcNow.ToString('o')
                entries = @($entries)
            }
            Write-Manifest -Manifest $manifest -Path $ManifestPath
            [ordered]@{ action = $Action; manifest = $ManifestPath; entries = @($entries) } | ConvertTo-Json -Depth 12
        }
        'Verify' {
            $manifest = Read-Manifest -Path $ManifestPath
            $selected = @(Get-SelectedEntries -Manifest $manifest -Numbers $PullRequest)
            $results = @()
            foreach ($entry in $selected) {
                $number = [int]$entry.pullRequest
                $pr = Get-CurrentPullRequest -Number $number
                $localOid = [string](@(Invoke-GitText -Arguments @('rev-parse', 'HEAD') -WorkingDirectory ([string]$entry.worktree))[0]).Trim()
                $remoteOid = [string]$pr.headRefOid
                if ($localOid -ne $remoteOid) {
                    throw "PR #$number is not fully published: local=$localOid, remote=$remoteOid."
                }
                if (-not (Test-CleanWorktree -Path ([string]$entry.worktree))) {
                    throw "Review worktree for PR #$number is dirty; commit or discard the local changes before completion."
                }
                $results += [ordered]@{
                    pullRequest = $number
                    worktree = [string]$entry.worktree
                    head = $localOid
                    remoteHead = $remoteOid
                    clean = $true
                    published = $true
                }
            }
            [ordered]@{ action = $Action; manifest = $ManifestPath; results = @($results) } | ConvertTo-Json -Depth 12
        }
        'Cleanup' {
            $manifest = Read-Manifest -Path $ManifestPath
            $selected = @(Get-SelectedEntries -Manifest $manifest -Numbers $PullRequest)
            foreach ($entry in $selected) {
                if (-not (Test-CleanWorktree -Path ([string]$entry.worktree)) -and -not $Force) {
                    throw "Review worktree for PR #$($entry.pullRequest) is dirty; use -Force only to discard it."
                }
            }
            foreach ($entry in $selected) {
                Remove-PreparedEntry -Entry $entry -RepositoryRoot $repositoryRoot -AllowDirty:$Force
            }
            $remaining = @($manifest.entries | Where-Object { $selected.pullRequest -notcontains $_.pullRequest })
            $manifest.entries = $remaining
            Write-Manifest -Manifest $manifest -Path $ManifestPath
            [ordered]@{ action = $Action; manifest = $ManifestPath; removed = @($selected.pullRequest); remaining = @($remaining.pullRequest) } | ConvertTo-Json -Depth 12
        }
    }
} catch {
    $position = $_.InvocationInfo.PositionMessage
    Write-Error "PR review worktree operation failed: $($_.Exception.Message)${position}"
    exit 1
}
