$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot '../pr-preflight.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-GhJson', 'Get-RipwireChangedPathCount', 'Invoke-RipwireGate')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing $name production function." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Fail-Preflight([string]$Message) { throw $Message }

function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

$temp = Join-Path $env:TEMP ('gxmcp-ripwire-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $gitRepo = Join-Path $temp 'repo'
    New-Item -ItemType Directory -Path $gitRepo -Force | Out-Null
    & git -C $gitRepo init -q
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the temporary Git repository.' }
    & git -C $gitRepo config user.email 'test@example.invalid'
    & git -C $gitRepo config user.name 'ripwire-test'
    Set-Content -LiteralPath (Join-Path $gitRepo 'tracked.txt') -Value 'base' -Encoding utf8
    & git -C $gitRepo add tracked.txt
    & git -C $gitRepo commit -q -m base
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the temporary Git baseline.' }
    & git -C $gitRepo branch base
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the temporary Git base ref.' }
    Set-Content -LiteralPath (Join-Path $gitRepo 'tracked.txt') -Value 'staged' -Encoding utf8
    & git -C $gitRepo add tracked.txt
    Set-Content -LiteralPath (Join-Path $gitRepo 'untracked.txt') -Value 'worktree' -Encoding utf8

    $fakeGh = Join-Path $temp 'gh.cmd'
    @('@echo off', 'echo {"number":1,"state":"OPEN"}', 'exit /b 0') | Set-Content -LiteralPath $fakeGh -Encoding ascii
    $ghValue = Get-GhJson -Arguments @('issue', 'view', '1') -GhPath $fakeGh
    if ([int]$ghValue.number -ne 1) { throw 'Valid GitHub JSON was not returned.' }

    @('@echo off', 'exit /b 0') | Set-Content -LiteralPath $fakeGh -Encoding ascii
    Expect-Failure { Get-GhJson -Arguments @('issue', 'view', '1') -GhPath $fakeGh } 'Empty GitHub output was accepted.'

    @('@echo off', 'echo not-json', 'exit /b 0') | Set-Content -LiteralPath $fakeGh -Encoding ascii
    Expect-Failure { Get-GhJson -Arguments @('issue', 'view', '1') -GhPath $fakeGh } 'Invalid GitHub JSON was accepted.'

    $missing = Join-Path $temp 'missing.cmd'
    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $missing -RepositoryPath $gitRepo
    if ($result.status -ne 'skipped' -or $result.exitCode -ne 0) { throw 'Missing optional ripwire must be reported as skipped.' }

    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $missing -RepositoryPath $gitRepo -Require
    if ($result.status -ne 'failed' -or $result.exitCode -ne 127) { throw 'Missing required ripwire must fail with exit code 127.' }

    $fake = Join-Path $temp 'ripwire.cmd'
    @('@echo off', 'echo ^<pr-context files="1" /^>', 'exit /b 0') | Set-Content -LiteralPath $fake -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $fake -RepositoryPath $gitRepo
    if ($result.status -ne 'passed' -or $result.exitCode -ne 0) { throw 'Successful ripwire must be reported as passed.' }

    @('@echo off', 'echo ^<pr-context files="0" /^>', 'exit /b 0') | Set-Content -LiteralPath $fake -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $fake -RepositoryPath $gitRepo
    if ($result.status -ne 'failed' -or $result.exitCode -ne 65 -or $result.reason -notmatch 'files=0') {
        throw 'A zero ripwire file count must fail when Git exposes changed paths.'
    }

    @('@echo off', 'echo ^<pr-context files="1" /^>', 'exit /b 7') | Set-Content -LiteralPath $fake -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $fake -RepositoryPath $gitRepo
    if ($result.status -ne 'failed' -or $result.exitCode -ne 7) { throw 'Failed ripwire must preserve its exit code.' }

    @('@echo off', 'echo ripwire: could not materialize the requested tree', 'exit /b 0') | Set-Content -LiteralPath $fake -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $fake -RepositoryPath $gitRepo
    if ($result.status -ne 'failed' -or $result.exitCode -ne 65) { throw 'Tree-materialization errors must fail even with exit code 0.' }

    @('@echo off', 'exit /b 0') | Set-Content -LiteralPath $fake -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'base' -RipwirePath $fake -RepositoryPath $gitRepo
    if ($result.status -ne 'failed' -or $result.exitCode -ne 65) { throw 'An empty ripwire response must fail closed.' }

    Write-Host 'pr-preflight ripwire policy: absent, valid, exit failure, materialization failure and empty-output paths passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
