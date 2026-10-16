[CmdletBinding()]
param([switch] $NoBuild)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7+ so this runner can terminate its entire owned test process tree if the memory reserve is threatened.' }
. (Join-Path $PSScriptRoot 'EnterpriseFixture.Common.ps1')
$enterpriseLock = Lock-EnterpriseFixture
$enterprisePrevious = @{}
$enterpriseValues = $null
try {
    $enterpriseValues = Read-EnterpriseFixtureEnvironment
    Assert-EnterpriseFixtureResources -EnvironmentValues $enterpriseValues
    foreach ($enterpriseKey in $enterpriseValues.Keys) {
        $enterprisePrevious[$enterpriseKey] = [Environment]::GetEnvironmentVariable($enterpriseKey, 'Process')
        [Environment]::SetEnvironmentVariable($enterpriseKey, $enterpriseValues[$enterpriseKey], 'Process')
    }
    $enterpriseArguments = @('test', (Join-Path $enterpriseRepoRoot 'tests/GraphBridge.EnterpriseIntegrationTests/GraphBridge.EnterpriseIntegrationTests.csproj'),
        '--no-restore', '--logger', 'console;verbosity=detailed', '--results-directory', (Join-Path $enterpriseStateDirectory 'TestResults'))
    if ($NoBuild) { $enterpriseArguments += '--no-build' }
    Invoke-EnterpriseSupervisedProcess -FilePath 'dotnet' -Arguments $enterpriseArguments -LogStem 'test'
}
catch {
    # The supervised test tree has already been terminated. If that did not restore the reserve,
    # release only this fixture's now-idle server; retain its env for dedicated teardown.
    if ($null -ne $enterpriseValues) {
        try {
            $enterpriseReserve = 5GB
            if (Test-FixtureDedicatedHostedRunner) { $enterpriseReserve = 2GB }
            $enterpriseReleaseIdle = $true
            try { $enterpriseReleaseIdle = (Get-FixtureAvailableMemoryBytes) -lt $enterpriseReserve }
            catch { $enterpriseReleaseIdle = $true }
            if ($enterpriseReleaseIdle -and $null -ne (Get-EnterpriseTeardownContainer -EnvironmentValues $enterpriseValues)) {
                Invoke-EnterpriseCompose -EnvironmentValues $enterpriseValues -Arguments @('stop', '-t', '10')
            }
        }
        catch { Write-Warning 'Owned Enterprise idle-resource cleanup could not be confirmed; the original test failure is preserved.' }
    }
    throw
}
finally {
    . (Join-Path $PSScriptRoot '../../eng/scripts/Restore-ProcessEnvironment.ps1')
    Restore-ProcessEnvironment -Snapshot $enterprisePrevious
    $enterpriseLock.Dispose()
}
