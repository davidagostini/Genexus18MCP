[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',
    [string]$RepositoryPath = '.',
    [ValidateRange(2000, 50000)]
    [int]$TokenBudget = 12000,
    [string[]]$Scope = @(),
    [switch]$QualityDelta,
    [switch]$RequireRipwire,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& git -C $RepositoryPath @Arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$RepositoryPath' $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    return $output
}

function Get-ChangedPaths {
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($arguments in @(
        @('diff', '--name-only', "$BaseRef...HEAD"),
        @('diff', '--name-only'),
        @('diff', '--cached', '--name-only'),
        @('ls-files', '--others', '--exclude-standard')
    )) {
        foreach ($path in @(Invoke-GitText -Arguments $arguments)) {
            if (-not [string]::IsNullOrWhiteSpace($path)) {
                [void]$paths.Add($path.Trim().Replace('\', '/'))
            }
        }
    }
    return @($paths | Select-Object -Unique | Sort-Object)
}

function Get-QualityScopes {
    param([string[]]$Paths)

    $scopes = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Paths) {
        if ($path -match '^(src|cli|scripts|install\.ps1$|build\.ps1$)' -and
            $path -notmatch '(^|/)(bin|obj|publish|TestResults|tests?)(/|$)' -and
            $path -notmatch '(\.Tests/|\.test\.(js|ps1)$)') {
            if ($path -match '/') {
                [void]$scopes.Add((Split-Path -Parent $path).Replace('\', '/') + '/**')
            } else {
                [void]$scopes.Add($path)
            }
        }
    }
    return @($scopes | Select-Object -Unique | Sort-Object)
}

function Invoke-RipwireReport {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$ReportPath
    )

    $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllText(
        $ReportPath,
        (($output -join [Environment]::NewLine) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    if ($exitCode -ne 0) {
        throw "ripwire report failed with exit code $exitCode. Full output: $ReportPath"
    }
    return $output
}

try {
    $repositoryRootOutput = @(& git -C $RepositoryPath rev-parse --show-toplevel 2>&1)
    if ($LASTEXITCODE -ne 0 -or $repositoryRootOutput.Count -ne 1) {
        throw "'$RepositoryPath' is not a Git repository."
    }
    $RepositoryPath = [IO.Path]::GetFullPath($repositoryRootOutput[0].Trim())
    $changed = @(Get-ChangedPaths)
    if ($changed.Count -eq 0) {
        throw "No changed paths were found against '$BaseRef' or in the worktree."
    }

    $ripwireCommand = Get-Command ripwire -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $ripwireCommand) {
        if ($RequireRipwire) { throw 'ripwire is not found on PATH.' }
        [ordered]@{ status = 'skipped'; reason = 'ripwire is not found on PATH.' } | ConvertTo-Json -Depth 8
        exit 0
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $env:TEMP ('gxmcp-ripwire-review-' + [guid]::NewGuid().ToString('N'))
    }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

    $contextPath = Join-Path $OutputDirectory 'pr-context.xml'
    $contextArguments = @(
        $RepositoryPath,
        "--pr-context=$BaseRef",
        "--token-budget=$TokenBudget"
    )
    [void](Invoke-RipwireReport -Executable $ripwireCommand.Source -Arguments $contextArguments -ReportPath $contextPath)

    $qualityPath = $null
    $qualityScopes = @($Scope)
    if ($qualityScopes.Count -eq 0) {
        $qualityScopes = @(Get-QualityScopes -Paths $changed)
    }
    if ($QualityDelta -and $qualityScopes.Count -gt 0) {
        $qualityPath = Join-Path $OutputDirectory 'quality-delta.xml'
        $qualityArguments = @(
            $RepositoryPath,
            "--quality-delta=$BaseRef",
            "--token-budget=$TokenBudget",
            "--scope=$($qualityScopes -join ',')"
        )
        [void](Invoke-RipwireReport -Executable $ripwireCommand.Source -Arguments $qualityArguments -ReportPath $qualityPath)
    }

    [ordered]@{
        status = 'passed'
        baseRef = $BaseRef
        repository = $RepositoryPath
        tokenBudget = $TokenBudget
        changedFiles = $changed
        qualityScopes = $qualityScopes
        prContextPath = $contextPath
        qualityDeltaPath = $qualityPath
    } | ConvertTo-Json -Depth 12
} catch {
    Write-Error "Scoped ripwire review failed: $($_.Exception.Message)"
    exit 1
}
