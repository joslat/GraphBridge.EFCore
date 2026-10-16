[CmdletBinding()]
param(
    [string] $Filter,
    [switch] $NoBuild,
    [ValidateSet('Integration', 'Specification', 'Tooling', 'Workshop')]
    [string] $Suite = 'Integration',
    [ValidateSet('minimal', 'normal', 'detailed')][string] $Verbosity = 'detailed',
    [string] $ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$fixtureEngRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../eng')).Path
$fixtureRepoRoot = Split-Path -Parent $fixtureEngRoot
$fixtureEnvPath = Join-Path $fixtureEngRoot '.fixture.env'
if (-not (Test-Path -LiteralPath $fixtureEnvPath)) {
    throw 'Start the dedicated fixture first with eng/scripts/Start-Neo4jFixture.ps1.'
}
. (Join-Path $PSScriptRoot '../../eng/scripts/Assert-FixtureResources.ps1')
$fixtureComposeProject = Get-FixtureComposeProjectFromEnvironmentFile -Path $fixtureEnvPath
Assert-FixtureResources -Mode ExistingFixture -FixtureProject $fixtureComposeProject

$fixturePreviousEnvironment = @{}
try {
    foreach ($fixtureLine in [System.IO.File]::ReadAllLines($fixtureEnvPath)) {
        if ($fixtureLine -match '^(GRAPHBRIDGE_[A-Z0-9_]+)=(.*)$') {
            $fixtureVariable = $Matches[1]
            $fixturePreviousEnvironment[$fixtureVariable] = [Environment]::GetEnvironmentVariable($fixtureVariable, 'Process')
            [Environment]::SetEnvironmentVariable($fixtureVariable, $Matches[2], 'Process')
        }
    }

    $fixtureTestProject = if ($Suite -eq 'Specification') {
        'tests/GraphBridge.SpecificationTests/GraphBridge.SpecificationTests.csproj'
    } elseif ($Suite -eq 'Tooling') {
        'tests/GraphBridge.Tooling.IntegrationTests/GraphBridge.Tooling.IntegrationTests.csproj'
    } elseif ($Suite -eq 'Workshop') {
        'tests/GraphBridge.Workshop.Tests/GraphBridge.Workshop.Tests.csproj'
    } else {
        'tests/GraphBridge.IntegrationTests/GraphBridge.IntegrationTests.csproj'
    }
    $fixtureTestArguments = @('test', (Join-Path $fixtureRepoRoot $fixtureTestProject), '--no-restore', '--logger', "console;verbosity=$Verbosity")
    if ($ResultsDirectory) { $fixtureTestArguments += @('--logger', "trx;LogFileName=$Suite.trx", '--results-directory', $ResultsDirectory) }
    if ($Filter) { $fixtureTestArguments += @('--filter', $Filter) }
    if ($NoBuild) { $fixtureTestArguments += '--no-build' }
    & dotnet @fixtureTestArguments
    $fixtureExitCode = $LASTEXITCODE
}
finally {
    . (Join-Path $PSScriptRoot '../../eng/scripts/Restore-ProcessEnvironment.ps1')
    Restore-ProcessEnvironment -Snapshot $fixturePreviousEnvironment
}

if ($fixtureExitCode -ne 0) { throw "Integration test command failed with exit code $fixtureExitCode." }
