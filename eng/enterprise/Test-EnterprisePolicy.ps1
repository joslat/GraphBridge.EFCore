[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnterpriseFixture.Common.ps1')
$enterprisePolicyPasses = 0
$enterprisePolicyDockerCalls = 0
$enterprisePolicyThrowDocker = $false
$enterprisePolicyInspect = ''
$enterprisePolicyContainers = @()
$enterprisePolicyExpectedEnvironment = $null
$enterprisePolicyPrior = @{}
$enterprisePolicyFile = Join-Path $enterpriseRepoRoot ('.artifacts/enterprise-policy-' + [Guid]::NewGuid().ToString('N') + '.env')
$enterpriseEnvironmentPath = $enterprisePolicyFile

# Shadow the external executable. Any unexpected Docker shape fails; no Docker/process workload runs.
function docker {
    $script:enterprisePolicyDockerCalls++
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'compose') {
        foreach ($enterprisePolicyKey in $script:enterprisePolicyExpectedEnvironment.Keys) {
            if ([Environment]::GetEnvironmentVariable($enterprisePolicyKey, 'Process') -cne $script:enterprisePolicyExpectedEnvironment[$enterprisePolicyKey]) {
                throw 'Compose observed an inherited override instead of the explicitly generated environment.'
            }
        }
        if ($script:enterprisePolicyThrowDocker) { throw 'Synthetic Compose failure' }
        return
    }
    if ($args[0] -eq 'inspect') { return $script:enterprisePolicyInspect }
    if ($args[0] -eq 'ps') { return $script:enterprisePolicyContainers }
    throw 'Unexpected fake Docker invocation; external Docker is never allowed in policy checks.'
}

function Test-EnterpriseCase {
    param([string] $Name, [scriptblock] $Body, [string] $Failure)
    $enterprisePolicyError = $null
    try { & $Body | Out-Null }
    catch { $enterprisePolicyError = $_.Exception.Message }
    if ([string]::IsNullOrEmpty($Failure)) {
        if ($null -ne $enterprisePolicyError) { throw ('Policy case failed: ' + $Name + ': ' + $enterprisePolicyError) }
    }
    elseif ($null -eq $enterprisePolicyError -or $enterprisePolicyError.IndexOf($Failure, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw ('Policy case failed for an unexpected reason: ' + $Name)
    }
    $script:enterprisePolicyPasses++
    Write-Output ('PASS: ' + $Name)
}

function Write-EnterprisePolicyEnvironment {
    param([hashtable] $Values)
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $enterprisePolicyFile)) | Out-Null
    [System.IO.File]::WriteAllLines($enterprisePolicyFile, @($Values.Keys | Sort-Object | ForEach-Object { $_ + '=' + $Values[$_] }),
        [System.Text.UTF8Encoding]::new($false))
}

$enterprisePolicyValues = @{
    GRAPHBRIDGE_ENTERPRISE_INTEGRATION = '1'; GRAPHBRIDGE_ENTERPRISE_EDITION = 'enterprise'; GRAPHBRIDGE_ENTERPRISE_LICENSE = 'eval'
    GRAPHBRIDGE_ENTERPRISE_IMAGE = $enterpriseApprovedImage; GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID = ('a' * 32)
    GRAPHBRIDGE_ENTERPRISE_PROJECT = ('graphbridge-enterprise-' + ('a' * 32)); GRAPHBRIDGE_ENTERPRISE_URI = 'bolt://127.0.0.1:18687'
    GRAPHBRIDGE_ENTERPRISE_DATABASE = 'neo4j'; GRAPHBRIDGE_ENTERPRISE_ADMIN_PASSWORD = ('A' * 64)
    GRAPHBRIDGE_ENTERPRISE_READER_PASSWORD = ('B' * 64); GRAPHBRIDGE_ENTERPRISE_WRITER_PASSWORD = ('C' * 64)
    GRAPHBRIDGE_ENTERPRISE_NO_SCHEMA_PASSWORD = ('D' * 64)
}
$enterprisePolicyOwned = [pscustomobject]@{ Id = ('b' * 64); Project = $enterprisePolicyValues.GRAPHBRIDGE_ENTERPRISE_PROJECT; Service = 'neo4j'; Disposable = 'true' }
$enterprisePolicyForeign = [pscustomobject]@{ Id = ('c' * 64); Project = 'other-developer'; Service = 'neo4j'; Disposable = 'false' }
try {
    foreach ($enterprisePolicyKey in @($enterprisePolicyValues.Keys) + @('GRAPHBRIDGE_DEDICATED_HOSTED_RUNNER')) {
        $enterprisePolicyPrior[$enterprisePolicyKey] = [Environment]::GetEnvironmentVariable($enterprisePolicyKey, 'Process')
    }
    [Environment]::SetEnvironmentVariable('GRAPHBRIDGE_DEDICATED_HOSTED_RUNNER', '', 'Process')
    [Environment]::SetEnvironmentVariable('GRAPHBRIDGE_ENTERPRISE_LICENSE', 'yes', 'Process')
    Test-EnterpriseCase 'Missing license cannot inherit commercial acceptance' {
        Assert-EnterpriseStartupSelection -Edition enterprise -LicenseAcceptance '' -Image $enterpriseApprovedImage
    } 'Explicit Enterprise edition'
    Test-EnterpriseCase 'Missing edition is rejected' {
        Assert-EnterpriseStartupSelection -Edition '' -LicenseAcceptance eval -Image $enterpriseApprovedImage
    } 'Explicit Enterprise edition'
    Test-EnterpriseCase 'Unpinned or wrong image is rejected' {
        Assert-EnterpriseStartupSelection -Edition enterprise -LicenseAcceptance eval -Image 'neo4j:latest'
    } 'approved digest-pinned image'
    foreach ($enterprisePolicyChoice in @('yes', 'eval')) {
        Test-EnterpriseCase ('Explicit supported license choice: ' + $enterprisePolicyChoice) {
            Assert-EnterpriseStartupSelection -Edition enterprise -LicenseAcceptance $enterprisePolicyChoice -Image $enterpriseApprovedImage
        }
    }
    Test-EnterpriseCase 'Actual startup rejects wrong image before any Docker call' {
        $enterprisePolicyBefore = $script:enterprisePolicyDockerCalls
        try { & (Join-Path $PSScriptRoot 'Start-EnterpriseFixture.ps1') -Edition enterprise -LicenseAcceptance eval -Image 'wrong-image' }
        finally { if ($script:enterprisePolicyDockerCalls -ne $enterprisePolicyBefore) { throw 'Startup touched Docker before input validation.' } }
    } 'approved digest-pinned image'

    Write-EnterprisePolicyEnvironment $enterprisePolicyValues
    Test-EnterpriseCase 'Complete exact fixture environment is accepted' { Read-EnterpriseFixtureEnvironment }
    foreach ($enterprisePolicyFault in @('missing-secret', 'invalid-secret', 'missing-optin', 'disabled-optin', 'unknown-key', 'wrong-image', 'wrong-project', 'wrong-port')) {
        $enterprisePolicyBroken = $enterprisePolicyValues.Clone()
        switch ($enterprisePolicyFault) {
            'missing-secret' { $enterprisePolicyBroken.Remove('GRAPHBRIDGE_ENTERPRISE_READER_PASSWORD') }
            'invalid-secret' { $enterprisePolicyBroken.GRAPHBRIDGE_ENTERPRISE_READER_PASSWORD = 'not-a-generated-secret' }
            'missing-optin' { $enterprisePolicyBroken.Remove('GRAPHBRIDGE_ENTERPRISE_INTEGRATION') }
            'disabled-optin' { $enterprisePolicyBroken.GRAPHBRIDGE_ENTERPRISE_INTEGRATION = '0' }
            'unknown-key' { $enterprisePolicyBroken.GRAPHBRIDGE_ENTERPRISE_UNREVIEWED = '1' }
            'wrong-image' { $enterprisePolicyBroken.GRAPHBRIDGE_ENTERPRISE_IMAGE = 'neo4j:latest' }
            'wrong-project' { $enterprisePolicyBroken.GRAPHBRIDGE_ENTERPRISE_PROJECT = 'other-developer' }
            'wrong-port' { $enterprisePolicyBroken.GRAPHBRIDGE_ENTERPRISE_URI = 'bolt://127.0.0.1:17687' }
        }
        Write-EnterprisePolicyEnvironment $enterprisePolicyBroken
        Test-EnterpriseCase ('Invalid environment rejected: ' + $enterprisePolicyFault) { Read-EnterpriseFixtureEnvironment } 'Enterprise fixture'
    }
    Write-EnterprisePolicyEnvironment $enterprisePolicyValues
    $script:enterprisePolicyExpectedEnvironment = $enterprisePolicyValues
    [Environment]::SetEnvironmentVariable('GRAPHBRIDGE_ENTERPRISE_IMAGE', 'unapproved-inherited-image', 'Process')
    [Environment]::SetEnvironmentVariable('GRAPHBRIDGE_ENTERPRISE_ADMIN_PASSWORD', 'inherited-secret', 'Process')
    Test-EnterpriseCase 'Compose observes explicit eval/image/credentials and restores inherited values afterward' {
        Invoke-EnterpriseCompose -EnvironmentValues (Read-EnterpriseFixtureEnvironment) -Arguments @('up')
        if ($env:GRAPHBRIDGE_ENTERPRISE_LICENSE -cne 'yes' -or $env:GRAPHBRIDGE_ENTERPRISE_IMAGE -cne 'unapproved-inherited-image' -or
            $env:GRAPHBRIDGE_ENTERPRISE_ADMIN_PASSWORD -cne 'inherited-secret') { throw 'Compose environment was not restored.' }
    }
    $script:enterprisePolicyThrowDocker = $true
    Test-EnterpriseCase 'Compose failure still restores inherited environment' {
        try { Invoke-EnterpriseCompose -EnvironmentValues (Read-EnterpriseFixtureEnvironment) -Arguments @('down') }
        finally { if ($env:GRAPHBRIDGE_ENTERPRISE_LICENSE -cne 'yes') { throw 'Failure leaked license environment.' } }
    } 'Synthetic Compose failure'
    $script:enterprisePolicyThrowDocker = $false

    Test-EnterpriseCase 'Startup permits unrelated containers at the exact 7.5 GiB threshold' {
        Assert-EnterpriseFixtureResources -Startup -SnapshotProvider { [pscustomobject]@{ AvailableMemoryBytes = 7GB + 512MB; RunningContainers = @($enterprisePolicyForeign) } }
    }
    Test-EnterpriseCase 'Startup rejects one byte below reserve plus fixture and headroom' {
        Assert-EnterpriseFixtureResources -Startup -SnapshotProvider { [pscustomobject]@{ AvailableMemoryBytes = 7GB + 512MB - 1; RunningContainers = @() } }
    } 'work deferred'
    Test-EnterpriseCase 'Unmeasurable memory fails closed' {
        Assert-EnterpriseFixtureResources -Startup -SnapshotProvider { [pscustomobject]@{ AvailableMemoryBytes = $null; RunningContainers = @() } }
    } 'inspection is incomplete'
    $script:enterprisePolicyInspect = 'true|' + $enterprisePolicyValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID + '|' + $enterpriseApprovedImage
    Test-EnterpriseCase 'Existing owned fixture permits other developer containers' {
        Assert-EnterpriseFixtureResources -EnvironmentValues $enterprisePolicyValues -SnapshotProvider {
            [pscustomobject]@{ AvailableMemoryBytes = 5GB + 512MB; RunningContainers = @($enterprisePolicyOwned, $enterprisePolicyForeign) }
        }
    }
    Test-EnterpriseCase 'Existing fixture rejects missing selected ownership' {
        Assert-EnterpriseFixtureResources -EnvironmentValues $enterprisePolicyValues -SnapshotProvider {
            [pscustomobject]@{ AvailableMemoryBytes = 16GB; RunningContainers = @($enterprisePolicyForeign) }
        }
    } 'ownership is missing'
    Test-EnterpriseCase 'Existing fixture rejects ambiguous selected ownership' {
        Assert-EnterpriseFixtureResources -EnvironmentValues $enterprisePolicyValues -SnapshotProvider {
            [pscustomobject]@{ AvailableMemoryBytes = 16GB; RunningContainers = @($enterprisePolicyOwned, $enterprisePolicyOwned) }
        }
    } 'ownership is missing or ambiguous'
    $script:enterprisePolicyInspect = 'true|' + $enterprisePolicyValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID + '|unapproved-image'
    Test-EnterpriseCase 'Container image mismatch fails ownership inspection' {
        Assert-EnterpriseFixtureResources -EnvironmentValues $enterprisePolicyValues -SnapshotProvider {
            [pscustomobject]@{ AvailableMemoryBytes = 16GB; RunningContainers = @($enterprisePolicyOwned) }
        }
    } 'not the exact owned Enterprise fixture'
    $script:enterprisePolicyContainers = @()
    Test-EnterpriseCase 'No-container startup failure has a safe empty teardown selection' {
        if ($null -ne (Get-EnterpriseTeardownContainer -EnvironmentValues $enterprisePolicyValues)) { throw 'Unexpected selected container.' }
    }
    $script:enterprisePolicyContainers = @(('b' * 64))
    $script:enterprisePolicyInspect = 'true|true|' + ('f' * 32) + '|' + $enterpriseApprovedImage
    Test-EnterpriseCase 'Teardown rejects another fixture marker' {
        Get-EnterpriseTeardownContainer -EnvironmentValues $enterprisePolicyValues
    } 'ownership mismatch'
    Write-Output "$enterprisePolicyPasses Enterprise policy cases passed; synthetic snapshots and fake Docker only, no server/license/process workload executed."
}
finally {
    foreach ($enterprisePolicyKey in $enterprisePolicyPrior.Keys) {
        [Environment]::SetEnvironmentVariable($enterprisePolicyKey, $enterprisePolicyPrior[$enterprisePolicyKey], 'Process')
    }
    if (Test-Path -LiteralPath $enterprisePolicyFile) {
        $enterprisePolicyResolved = (Resolve-Path -LiteralPath $enterprisePolicyFile).Path
        if ($enterprisePolicyResolved -cne [System.IO.Path]::GetFullPath($enterprisePolicyFile)) { throw 'Unexpected policy fixture file path.' }
        Remove-Item -LiteralPath $enterprisePolicyResolved -Force
    }
}
