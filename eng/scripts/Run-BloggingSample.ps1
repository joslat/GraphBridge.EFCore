[CmdletBinding()]
param([switch] $PrepareSchema, [switch] $NoBuild)

$ErrorActionPreference = 'Stop'
$sampleRepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sampleEnvPath = Join-Path $sampleRepoRoot 'eng/.fixture.env'
if (-not (Test-Path -LiteralPath $sampleEnvPath)) { throw 'Start the disposable fixture first.' }
. (Join-Path $PSScriptRoot 'Assert-FixtureResources.ps1')
$sampleComposeProject = Get-FixtureComposeProjectFromEnvironmentFile -Path $sampleEnvPath
Assert-FixtureResources -Mode ExistingFixture -FixtureProject $sampleComposeProject
$samplePreviousEnvironment = @{}
try {
    foreach ($sampleLine in [System.IO.File]::ReadAllLines($sampleEnvPath)) {
        if ($sampleLine -match '^(GRAPHBRIDGE_[A-Z0-9_]+)=(.*)$') {
            $sampleVariable = $Matches[1]
            $samplePreviousEnvironment[$sampleVariable] = [Environment]::GetEnvironmentVariable($sampleVariable, 'Process')
            [Environment]::SetEnvironmentVariable($sampleVariable, $Matches[2], 'Process')
        }
    }
    $sampleArguments = @('run', '--no-restore', '--project', (Join-Path $sampleRepoRoot 'samples/01-Blogging-CodeFirst/Blogging.App'))
    if ($NoBuild) { $sampleArguments += '--no-build' }
    $sampleArguments += @('--', '--run')
    if ($PrepareSchema) { $sampleArguments += '--prepare-schema' }
    & dotnet @sampleArguments
    if ($LASTEXITCODE -ne 0) { throw "Blogging sample failed with exit code $LASTEXITCODE." }
}
finally {
    . (Join-Path $PSScriptRoot 'Restore-ProcessEnvironment.ps1')
    Restore-ProcessEnvironment -Snapshot $samplePreviousEnvironment
}
