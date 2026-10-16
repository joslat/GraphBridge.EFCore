#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$policyRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$policyRoot = Join-Path $policyRepo ('.artifacts/quickstart-policy/' + [Guid]::NewGuid().ToString('N'))
$policyScripts = Join-Path $policyRoot 'eng/scripts'
[IO.Directory]::CreateDirectory($policyScripts) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../eng/scripts/Run-BloggingQuickstart.ps1') -Destination $policyScripts
$policyCommon = @'
function Test-FixtureDedicatedHostedRunner { return $false }
function Assert-FixtureResources { param($Mode); throw 'Synthetic resource refusal before any subprocess.' }
'@
Set-Content -LiteralPath (Join-Path $policyScripts 'Benchmark.Common.ps1') -Value $policyCommon -Encoding utf8
$policyStop = "throw 'STOP MUST NOT RUN for an existing fixture or before startup.'"
Set-Content -LiteralPath (Join-Path $policyScripts 'Stop-Neo4jFixture.ps1') -Value $policyStop -Encoding utf8
$policyEnv = Join-Path $policyRoot 'eng/.fixture.env'
$policyRunner = Join-Path $policyScripts 'Run-BloggingQuickstart.ps1'
$policyPasses = 0
function Assert-QuickstartRefusal {
    param([string] $Expected)
    $caught = $null
    try { & $policyRunner } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike "*$Expected*") {
        throw "Expected quickstart refusal was not observed: $Expected"
    }
    $script:policyPasses++
}

# A foreign/preexisting environment must be untouched; no child process or teardown is reached.
[IO.File]::WriteAllText($policyEnv, 'SYNTHETIC_EXISTING_ENVIRONMENT=preserve')
Assert-QuickstartRefusal 'environment already exists'
if ([IO.File]::ReadAllText($policyEnv) -cne 'SYNTHETIC_EXISTING_ENVIRONMENT=preserve') {
    throw 'Existing environment was changed.'
}
$policyPasses++
Remove-Item -LiteralPath $policyEnv -Force
Assert-QuickstartRefusal 'Synthetic resource refusal'
if (Test-Path -LiteralPath $policyEnv) { throw 'Resource refusal created fixture credentials.' }
$policyPasses++

# An independent handle prevents concurrent quickstarts and is not stolen by the runner.
$policyLockPath = Join-Path $policyRoot '.artifacts/blogging-quickstart.lock'
$policyLock = [IO.File]::Open($policyLockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $caught = $null
    try { & $policyRunner } catch { $caught = $_ }
    if ($null -eq $caught) { throw 'Concurrent quickstart lock was not enforced.' }
    $policyLock.WriteByte(1)
    $policyPasses++
} finally { $policyLock.Dispose() }

# Reuse real parser checks; synthetic tests above never launch Docker or dotnet.
foreach ($policyPath in @((Join-Path $PSScriptRoot '../../eng/scripts/Run-BloggingQuickstart.ps1'), (Join-Path $PSScriptRoot '../../eng/scripts/Run-BloggingSample.ps1'))) {
    $policyTokens = $null
    $policyErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($policyPath, [ref] $policyTokens, [ref] $policyErrors) | Out-Null
    if ($policyErrors.Count -ne 0) { throw 'Quickstart/sample script parse failed.' }
    $policyPasses++
}
Write-Output "$policyPasses quickstart guard/parser assertions passed; no database or build started."
