[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OursPath,
    [Parameter(Mandatory = $true)][string]$TheirsPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$categoryOrder = @('Added', 'Tracked issues', 'Changed', 'Fixed', 'Removed', 'Internal')

function Read-ChangelogDocument {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Changelog input '$Path' was not found."
    }
    $text = [IO.File]::ReadAllText($Path)
    if ($text -match '(?m)^(<<<<<<<|=======|>>>>>>>)') {
        throw "Changelog input '$Path' still contains conflict markers."
    }
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = [regex]::Split($text, '\r?\n')
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
        $lines = $lines[0..($lines.Count - 2)]
    }
    $start = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^##\s+Unreleased\s*$') {
            $start = $index
            break
        }
    }
    if ($start -lt 0) {
        throw "Changelog input '$Path' has no ## Unreleased section."
    }
    $end = $lines.Count
    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^##\s+') {
            $end = $index
            break
        }
    }
    return [pscustomobject]@{
        path = $Path
        lines = @($lines)
        newline = $newline
        unreleasedStart = $start
        unreleasedEnd = $end
    }
}

function Get-UnreleasedSections {
    param([Parameter(Mandatory = $true)]$Document)

    $preamble = [System.Collections.Generic.List[string]]::new()
    $sections = [ordered]@{}
    $current = $null
    for ($index = $Document.unreleasedStart + 1; $index -lt $Document.unreleasedEnd; $index++) {
        $line = [string]$Document.lines[$index]
        if ($line -match '^###\s+(.+?)\s*$') {
            $current = $Matches[1].Trim()
            if (-not $sections.Contains($current)) {
                $sections[$current] = [System.Collections.Generic.List[string]]::new()
            }
            continue
        }
        if ($null -eq $current) {
            [void]$preamble.Add($line)
        } else {
            [void]$sections[$current].Add($line)
        }
    }
    return [pscustomobject]@{ preamble = @($preamble); sections = $sections }
}

function Get-ContentBlocks {
    param([string[]]$Lines)

    $blocks = [System.Collections.Generic.List[object]]::new()
    $current = [System.Collections.Generic.List[string]]::new()
    foreach ($line in @($Lines)) {
        if ($line -match '^\s*[-*]\s+') {
            if ($current.Count -gt 0) {
                [void]$blocks.Add(@($current))
                $current = [System.Collections.Generic.List[string]]::new()
            }
            [void]$current.Add([string]$line)
        } elseif ($current.Count -gt 0) {
            [void]$current.Add([string]$line)
        } elseif (-not [string]::IsNullOrWhiteSpace([string]$line)) {
            [void]$current.Add([string]$line)
        }
    }
    if ($current.Count -gt 0) {
        [void]$blocks.Add(@($current))
    }
    return @($blocks)
}

function Merge-SectionContent {
    param(
        [string[]]$Ours,
        [string[]]$Theirs
    )

    $merged = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($source in @($Ours, $Theirs)) {
        foreach ($block in @(Get-ContentBlocks -Lines $source)) {
            $key = (($block -join "`n").Trim())
            if ([string]::IsNullOrWhiteSpace($key) -or -not $seen.Add($key)) {
                continue
            }
            if ($merged.Count -gt 0 -and $merged[$merged.Count - 1] -ne '') {
                [void]$merged.Add('')
            }
            foreach ($line in @($block)) {
                [void]$merged.Add([string]$line)
            }
        }
    }
    return @($merged)
}

function New-MergedUnreleased {
    param(
        [Parameter(Mandatory = $true)]$Ours,
        [Parameter(Mandatory = $true)]$Theirs
    )

    $oursSections = Get-UnreleasedSections -Document $Ours
    $theirsSections = Get-UnreleasedSections -Document $Theirs
    $body = [System.Collections.Generic.List[string]]::new()
    $preamble = @($oursSections.preamble | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($preamble.Count -eq 0) {
        $preamble = @($theirsSections.preamble | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    }
    foreach ($line in $preamble) { [void]$body.Add([string]$line) }

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $categoryOrder + @($oursSections.sections.Keys) + @($theirsSections.sections.Keys)) {
        if (-not $names.Contains([string]$name)) { [void]$names.Add([string]$name) }
    }
    foreach ($name in $names) {
        $oursLines = if ($oursSections.sections.Contains($name)) { @($oursSections.sections[$name]) } else { @() }
        $theirsLines = if ($theirsSections.sections.Contains($name)) { @($theirsSections.sections[$name]) } else { @() }
        $content = @(Merge-SectionContent -Ours $oursLines -Theirs $theirsLines)
        if ($content.Count -eq 0) { continue }
        while ($body.Count -gt 0 -and $body[$body.Count - 1] -eq '') { $body.RemoveAt($body.Count - 1) }
        if ($body.Count -gt 0) { [void]$body.Add('') }
        [void]$body.Add("### $name")
        [void]$body.Add('')
        foreach ($line in $content) { [void]$body.Add([string]$line) }
    }
    while ($body.Count -gt 0 -and $body[$body.Count - 1] -eq '') { $body.RemoveAt($body.Count - 1) }
    return @($body)
}

try {
    $ours = Read-ChangelogDocument -Path $OursPath
    $theirs = Read-ChangelogDocument -Path $TheirsPath
    if ((Test-Path -LiteralPath $OutputPath -PathType Leaf) -and -not $Force) {
        throw "Output '$OutputPath' already exists; pass -Force after reviewing the two inputs."
    }

    $mergedBody = @(New-MergedUnreleased -Ours $ours -Theirs $theirs)
    $result = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -le $ours.unreleasedStart; $index++) {
        [void]$result.Add([string]$ours.lines[$index])
    }
    while ($result.Count -gt 0 -and $result[$result.Count - 1] -eq '') { $result.RemoveAt($result.Count - 1) }
    [void]$result.Add('')
    foreach ($line in $mergedBody) { [void]$result.Add([string]$line) }
    if ($ours.unreleasedEnd -lt $ours.lines.Count) {
        [void]$result.Add('')
        for ($index = $ours.unreleasedEnd; $index -lt $ours.lines.Count; $index++) {
            [void]$result.Add([string]$ours.lines[$index])
        }
    }

    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = "$OutputPath.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText(
        $temporary,
        (($result -join $ours.newline) + $ours.newline),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $OutputPath -Force

    [ordered]@{
        status = 'merged'
        output = [IO.Path]::GetFullPath($OutputPath)
        categories = @((Get-UnreleasedSections -Document (Read-ChangelogDocument -Path $OutputPath)).sections.Keys)
    } | ConvertTo-Json -Depth 8
} catch {
    Write-Error "Changelog merge failed: $($_.Exception.Message)"
    exit 1
}
