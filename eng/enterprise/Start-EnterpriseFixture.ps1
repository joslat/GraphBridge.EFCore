[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('enterprise')][string] $Edition,
    [Parameter(Mandatory = $true)][ValidateSet('yes', 'eval')][string] $LicenseAcceptance,
    [Parameter(Mandatory = $true)][string] $Image
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnterpriseFixture.Common.ps1')
# No default or inherited environment choice can authorize Enterprise license acceptance.
Assert-EnterpriseStartupSelection -Edition $Edition -LicenseAcceptance $LicenseAcceptance -Image $Image
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Enterprise startup requires PowerShell 7+ for supervised resource checks.' }
$enterpriseLock = Lock-EnterpriseFixture
$enterpriseCreatedEnvironment = $false
try {
    if (Test-Path -LiteralPath $enterpriseEnvironmentPath) { throw 'An Enterprise fixture already exists; use its dedicated stop script first.' }
    Assert-EnterpriseFixtureResources -Startup
    $enterpriseId = [Guid]::NewGuid().ToString('N')
    $enterpriseProject = 'graphbridge-enterprise-' + $enterpriseId
    $enterpriseLines = @(
        'GRAPHBRIDGE_ENTERPRISE_INTEGRATION=1'
        'GRAPHBRIDGE_ENTERPRISE_EDITION=enterprise'
        "GRAPHBRIDGE_ENTERPRISE_LICENSE=$LicenseAcceptance"
        "GRAPHBRIDGE_ENTERPRISE_IMAGE=$Image"
        "GRAPHBRIDGE_ENTERPRISE_PROJECT=$enterpriseProject"
        "GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID=$enterpriseId"
        'GRAPHBRIDGE_ENTERPRISE_URI=bolt://127.0.0.1:18687'
        'GRAPHBRIDGE_ENTERPRISE_DATABASE=neo4j'
        ('GRAPHBRIDGE_ENTERPRISE_ADMIN_PASSWORD=' + (New-EnterpriseFixtureSecret))
        ('GRAPHBRIDGE_ENTERPRISE_READER_PASSWORD=' + (New-EnterpriseFixtureSecret))
        ('GRAPHBRIDGE_ENTERPRISE_WRITER_PASSWORD=' + (New-EnterpriseFixtureSecret))
        ('GRAPHBRIDGE_ENTERPRISE_NO_SCHEMA_PASSWORD=' + (New-EnterpriseFixtureSecret))
    )
    [System.IO.File]::WriteAllLines($enterpriseEnvironmentPath, $enterpriseLines, [System.Text.UTF8Encoding]::new($false))
    $enterpriseCreatedEnvironment = $true
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        & chmod 600 $enterpriseEnvironmentPath
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict Enterprise credential file permissions.' }
    }
    $enterpriseValues = Read-EnterpriseFixtureEnvironment
    Invoke-EnterpriseCompose -EnvironmentValues $enterpriseValues -Arguments @('up', '-d', '--wait', '--wait-timeout', '180') -SuperviseStartup
    Assert-EnterpriseFixtureResources -EnvironmentValues $enterpriseValues
    Invoke-EnterpriseCompose -EnvironmentValues $enterpriseValues -Arguments @('exec', '-T', 'neo4j', 'sh', '-c', 'exec cypher-shell -u neo4j -p "${NEO4J_AUTH#*/}"') `
        -InputText "CREATE (:GraphBridgeEnterpriseFixture {id: '$enterpriseId'});"
    Write-Output 'Owned Enterprise fixture ready on 127.0.0.1:18687. Credentials remain in ignored .artifacts/enterprise-fixture/fixture.env.'
}
catch {
    # A stopped startup client may already have created its server. Stop only a fully validated owned
    # container and retain the env file for explicit teardown/recovery; never touch another project.
    if ($enterpriseCreatedEnvironment -and (Test-Path -LiteralPath $enterpriseEnvironmentPath)) {
        try {
            $enterpriseFailedValues = Read-EnterpriseFixtureEnvironment
            if ($null -ne (Get-EnterpriseTeardownContainer -EnvironmentValues $enterpriseFailedValues)) {
                Invoke-EnterpriseCompose -EnvironmentValues $enterpriseFailedValues -Arguments @('stop', '-t', '10')
            }
        }
        catch { Write-Warning 'Owned Enterprise startup cleanup could not be confirmed; use the dedicated stop script after inspecting ownership.' }
    }
    throw
}
finally { $enterpriseLock.Dispose() }
