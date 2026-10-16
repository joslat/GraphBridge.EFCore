[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../eng/scripts/Assert-FixtureResources.ps1')
$resourceTestProject = 'graphbridge-fixture-' + ('a' * 32)
$resourceTestOwned = [pscustomobject]@{ Id = ('b' * 64); Project = $resourceTestProject; Service = 'neo4j'; Disposable = 'true' }
$resourceTestForeign = [pscustomobject]@{ Id = ('c' * 64); Project = 'another-repository'; Service = 'neo4j'; Disposable = 'false' }
$resourceTestOtherFixture = [pscustomobject]@{ Id = ('d' * 64); Project = 'graphbridge-fixture-' + ('e' * 32); Service = 'neo4j'; Disposable = 'true' }
$resourceTestPasses = 0

function Test-ResourcePolicyCase {
    param([string] $Name, [string] $Mode, [object] $Snapshot, [bool] $ShouldPass, [string] $ExpectedFailure,
        [switch] $DedicatedHostedRunner)
    $resourceCasePassed = $true
    $resourceFailureMessage = $null
    try {
        Assert-FixtureResources -Mode $Mode -FixtureProject $resourceTestProject -SnapshotProvider { $Snapshot } -DedicatedHostedRunner:$DedicatedHostedRunner
    }
    catch { $resourceCasePassed = $false; $resourceFailureMessage = $_.Exception.Message }
    if ($resourceCasePassed -ne $ShouldPass) { throw "Resource policy case failed: $Name" }
    if (-not $ShouldPass -and ([string]::IsNullOrWhiteSpace($ExpectedFailure) -or
        $resourceFailureMessage.IndexOf($ExpectedFailure, [StringComparison]::OrdinalIgnoreCase) -lt 0)) {
        throw "Resource policy case failed for an unexpected reason: $Name"
    }
    $script:resourceTestPasses++
    Write-Output "PASS: $Name"
}

Test-ResourcePolicyCase 'Startup allows exact 7 GiB threshold' Startup ([pscustomobject]@{
    AvailableMemoryBytes = 7GB; RunningContainers = @()
}) $true
Test-ResourcePolicyCase 'Startup permits other containers and graph servers without an override' Startup ([pscustomobject]@{
    AvailableMemoryBytes = 7GB; RunningContainers = @($resourceTestForeign, $resourceTestOtherFixture); NativeGraphProcessIds = @(123)
}) $true
Test-ResourcePolicyCase 'Startup preserves reserve and fixture budget' Startup ([pscustomobject]@{
    AvailableMemoryBytes = 7GB - 1; RunningContainers = @()
}) $false 'host RAM available'
Test-ResourcePolicyCase 'Existing fixture allows exact reserve and headroom' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 5GB + 512MB; RunningContainers = @($resourceTestOwned)
}) $true
Test-ResourcePolicyCase 'Existing fixture coexists with other containers and graph servers' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 6GB; RunningContainers = @($resourceTestOwned, $resourceTestForeign, $resourceTestOtherFixture); NativeGraphProcessIds = @(123)
}) $true
Test-ResourcePolicyCase 'Existing fixture rejects missing selected ownership' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 16GB; RunningContainers = @($resourceTestForeign, $resourceTestOtherFixture)
}) $false 'selected Compose project'
Test-ResourcePolicyCase 'Existing fixture rejects no running container' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 16GB; RunningContainers = @()
}) $false 'selected Compose project'
Test-ResourcePolicyCase 'Existing fixture rejects ambiguous selected service' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 16GB; RunningContainers = @($resourceTestOwned, $resourceTestOwned)
}) $false 'selected Compose project'
Test-ResourcePolicyCase 'Existing fixture rejects wrong Compose service' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 16GB
    RunningContainers = @([pscustomobject]@{ Id = ('b' * 64); Project = $resourceTestProject; Service = 'other-service'; Disposable = 'true' })
}) $false 'selected Compose project'
Test-ResourcePolicyCase 'Existing fixture rejects missing disposable label' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 16GB
    RunningContainers = @([pscustomobject]@{ Id = ('b' * 64); Project = $resourceTestProject; Service = 'neo4j'; Disposable = $null })
}) $false 'selected Compose project'
Test-ResourcePolicyCase 'Existing fixture preserves shared memory reserve' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 5GB + 512MB - 1; RunningContainers = @($resourceTestOwned, $resourceTestForeign)
}) $false 'host RAM available'
Test-ResourcePolicyCase 'Unknown memory fails closed' Startup ([pscustomobject]@{
    AvailableMemoryBytes = $null; RunningContainers = @()
}) $false 'Incomplete resource inspection'
Test-ResourcePolicyCase 'Unknown container inspection fails closed' Startup ([pscustomobject]@{
    AvailableMemoryBytes = 16GB; RunningContainers = $null
}) $false 'Incomplete resource inspection'
Test-ResourcePolicyCase 'Dedicated hosted startup allows exact 4 GiB budget' Startup ([pscustomobject]@{
    AvailableMemoryBytes = 4GB; RunningContainers = @()
}) $true -DedicatedHostedRunner
Test-ResourcePolicyCase 'Dedicated hosted startup rejects below budget' Startup ([pscustomobject]@{
    AvailableMemoryBytes = 4GB - 1; RunningContainers = @()
}) $false 'host RAM available' -DedicatedHostedRunner
Test-ResourcePolicyCase 'Dedicated hosted existing fixture allows exact reserve and headroom' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 2GB + 512MB; RunningContainers = @($resourceTestOwned)
}) $true -DedicatedHostedRunner
Test-ResourcePolicyCase 'Dedicated hosted existing fixture rejects below budget' ExistingFixture ([pscustomobject]@{
    AvailableMemoryBytes = 2GB + 512MB - 1; RunningContainers = @($resourceTestOwned)
}) $false 'host RAM available' -DedicatedHostedRunner

if (Test-FixtureDedicatedHostedRunner -Requested '' -GitHubActions 'true' -RunnerEnvironment 'github-hosted') { throw 'Hosted policy must be explicitly enabled.' }
if (-not (Test-FixtureDedicatedHostedRunner -Requested '1' -GitHubActions 'true' -RunnerEnvironment 'github-hosted')) { throw 'Hosted policy opt-in was not recognized.' }
foreach ($resourceRunnerCase in @(
    @{ GitHubActions = 'true'; RunnerEnvironment = 'self-hosted' },
    @{ GitHubActions = ''; RunnerEnvironment = 'github-hosted' }
)) {
    $resourceRunnerRejected = $false
    try { Test-FixtureDedicatedHostedRunner -Requested '1' @resourceRunnerCase | Out-Null }
    catch { $resourceRunnerRejected = $_.Exception.Message -like '*requires an explicitly opted-in GitHub-hosted*' }
    if (-not $resourceRunnerRejected) { throw 'Hosted policy must reject local and self-hosted environments.' }
}

Write-Output "$resourceTestPasses resource policy cases and 4 hosted-runner detection assertions passed; synthetic snapshots only, no Docker or process mutation."
