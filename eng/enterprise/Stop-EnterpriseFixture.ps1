[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnterpriseFixture.Common.ps1')
$enterpriseLock = Lock-EnterpriseFixture
try {
    $enterpriseValues = Read-EnterpriseFixtureEnvironment
    # Teardown must remain possible below the memory reserve. Check exact Compose labels, marker ID,
    # and configured image without starting any database or inspecting its secret environment.
    $enterpriseContainer = Get-EnterpriseTeardownContainer -EnvironmentValues $enterpriseValues
    $enterpriseNetworks = @(& docker network ls --no-trunc --filter ('label=com.docker.compose.project=' + $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_PROJECT) --format '{{.ID}}')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect selected Enterprise networks; nothing removed.' }
    foreach ($enterpriseNetwork in $enterpriseNetworks) {
        if ($enterpriseNetwork -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid selected Enterprise network identity.' }
        $enterpriseNetworkIdentity = @(& docker network inspect --format '{{index .Labels "org.graphbridge.disposable"}}|{{index .Labels "org.graphbridge.enterprise"}}|{{index .Labels "org.graphbridge.fixture.id"}}' $enterpriseNetwork)
        if ($LASTEXITCODE -ne 0 -or $enterpriseNetworkIdentity.Count -ne 1 -or
            $enterpriseNetworkIdentity[0] -cne ('true|true|' + $enterpriseValues.GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID)) {
            throw 'Enterprise network ownership mismatch; nothing removed.'
        }
    }
    if ($null -ne $enterpriseContainer -or $enterpriseNetworks.Count -gt 0) {
        Invoke-EnterpriseCompose -EnvironmentValues $enterpriseValues -Arguments @('down', '--volumes')
    }
    $enterpriseResolved = (Resolve-Path -LiteralPath $enterpriseEnvironmentPath).Path
    if ($enterpriseResolved -cne [System.IO.Path]::GetFullPath($enterpriseEnvironmentPath)) { throw 'Unexpected Enterprise environment path.' }
    Remove-Item -LiteralPath $enterpriseResolved -Force
    Write-Output 'Owned Enterprise fixture removed.'
}
finally { $enterpriseLock.Dispose() }
