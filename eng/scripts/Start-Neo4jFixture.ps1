[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixtureEngRoot = Split-Path -Parent $PSScriptRoot
$fixtureComposePath = Join-Path $fixtureEngRoot 'docker-compose.yml'
$fixtureEnvPath = Join-Path $fixtureEngRoot '.fixture.env'
. (Join-Path $PSScriptRoot 'Assert-FixtureResources.ps1')

# Inspect shared-machine resources before creating credentials or starting any container.
Assert-FixtureResources -Mode Startup

if (Test-Path -LiteralPath $fixtureEnvPath) {
    throw 'Fixture env already exists. Stop the previous fixture with Stop-Neo4jFixture.ps1 before starting a fresh one.'
}

# Bind only reserved loopback ports; let Docker fail if another process owns them.
$fixtureRandomBytes = [byte[]]::new(32)
$fixtureRandomGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $fixtureRandomGenerator.GetBytes($fixtureRandomBytes) }
finally { $fixtureRandomGenerator.Dispose() }
$fixturePassword = [BitConverter]::ToString($fixtureRandomBytes).Replace('-', '')
$fixtureId = [Guid]::NewGuid().ToString('N')
$fixtureComposeProject = "graphbridge-fixture-$fixtureId"
$fixtureEnvironment = @(
    'GRAPHBRIDGE_INTEGRATION=1'
    'GRAPHBRIDGE_NEO4J_URI=bolt://127.0.0.1:17687'
    'GRAPHBRIDGE_NEO4J_USERNAME=neo4j'
    "GRAPHBRIDGE_NEO4J_PASSWORD=$fixturePassword"
    'GRAPHBRIDGE_NEO4J_DATABASE=neo4j'
    "GRAPHBRIDGE_FIXTURE_ID=$fixtureId"
    "GRAPHBRIDGE_COMPOSE_PROJECT=$fixtureComposeProject"
)
[System.IO.File]::WriteAllLines($fixtureEnvPath, $fixtureEnvironment, [System.Text.UTF8Encoding]::new($false))
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    & chmod 600 $fixtureEnvPath
    if ($LASTEXITCODE -ne 0) { throw 'Failed to restrict fixture credential file permissions.' }
}

# Compose prefers this process's variables to --env-file, so a password loaded from an earlier fixture would start the
# container with it while the file holds the new one. Give Compose this fixture's values, then restore the caller's.
. (Join-Path $PSScriptRoot 'Restore-ProcessEnvironment.ps1')
$fixtureCallerEnvironment = @{}
foreach ($fixtureVariable in 'GRAPHBRIDGE_NEO4J_PASSWORD', 'GRAPHBRIDGE_COMPOSE_PROJECT') {
    $fixtureCallerEnvironment[$fixtureVariable] = [Environment]::GetEnvironmentVariable($fixtureVariable, 'Process')
}
try {
    [Environment]::SetEnvironmentVariable('GRAPHBRIDGE_NEO4J_PASSWORD', $fixturePassword, 'Process')
    [Environment]::SetEnvironmentVariable('GRAPHBRIDGE_COMPOSE_PROJECT', $fixtureComposeProject, 'Process')
    & docker compose --project-name $fixtureComposeProject -f $fixtureComposePath --env-file $fixtureEnvPath up -d --wait --wait-timeout 180
    if ($LASTEXITCODE -ne 0) {
        throw 'Disposable fixture startup failed. Stop-Neo4jFixture.ps1 removes only this Compose project; credentials were not printed.'
    }

    # The password stays inside the container environment. No credential is in command arguments.
    # The generated GUID is validated by construction; this startup marker is never test data.
    $fixtureMarkerQuery = "CREATE (:GraphBridgeFixture {id: '$fixtureId'});"
    $fixtureMarkerQuery | & docker compose --project-name $fixtureComposeProject -f $fixtureComposePath --env-file $fixtureEnvPath exec -T neo4j sh -c 'exec cypher-shell -u neo4j -p "${NEO4J_AUTH#*/}"'
    if ($LASTEXITCODE -ne 0) { throw 'Failed to write the disposable fixture safety marker.' }
}
finally {
    Restore-ProcessEnvironment -Snapshot $fixtureCallerEnvironment
}
Write-Output 'Disposable GraphBridge Neo4j fixture ready on 127.0.0.1:17687. Credentials are in ignored eng/.fixture.env.'
