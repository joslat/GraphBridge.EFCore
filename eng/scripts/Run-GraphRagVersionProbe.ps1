#requires -Version 7.0
# GraphRAG v2 (GR-51): runs the GraphRAG live tests and the samples' recorded real-model replays against another cached Neo4j
# image in its own owned fixture, then removes it. The image must already be cached; the probe never pulls implicitly.
# Evidence goes to .artifacts/graphrag-version-probe/<version>.
[CmdletBinding()]
param(
    [string] $Image = 'neo4j@sha256:f238979f3c80da6f383f2e4fe91be4dc3580d7ee3b7edec83d0b8783c53b74a4',
    [string] $Version = '2026.10.1'
)
$ErrorActionPreference = 'Stop'
$probeRepo = (Resolve-Path "$PSScriptRoot/../..").Path
Set-Location $probeRepo
[Environment]::CurrentDirectory = $probeRepo
. "$probeRepo/eng/scripts/Assert-FixtureResources.ps1"
$probeEnv = Join-Path $probeRepo 'eng/.fixture.env'
if (Test-Path -LiteralPath $probeEnv) { throw 'Stop the existing fixture first (eng/scripts/Stop-Neo4jFixture.ps1).' }
$probeOutput = Join-Path $probeRepo ".artifacts/graphrag-version-probe/$Version"
[IO.Directory]::CreateDirectory($probeOutput) | Out-Null
Assert-FixtureResources -Mode Startup
$probeIdentity = docker image inspect $Image --format '{{.Id}} {{json .RepoDigests}}'
if ($LASTEXITCODE) { throw 'Cached image missing; no implicit image pull.' }
$probeIdentity | Set-Content (Join-Path $probeOutput 'image.txt') -Encoding utf8
$probeOverride = Join-Path $probeOutput 'compose.override.yml'
[IO.File]::WriteAllText($probeOverride, "services:`n  neo4j:`n    image: $Image`n", [Text.UTF8Encoding]::new($false))
$probeId = [Guid]::NewGuid().ToString('N')
$probeProject = 'graphbridge-fixture-' + $probeId
$probePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$probeLines = @('GRAPHBRIDGE_INTEGRATION=1', 'GRAPHBRIDGE_NEO4J_URI=bolt://127.0.0.1:17687',
    'GRAPHBRIDGE_NEO4J_USERNAME=neo4j', "GRAPHBRIDGE_NEO4J_PASSWORD=$probePassword",
    'GRAPHBRIDGE_NEO4J_DATABASE=neo4j', "GRAPHBRIDGE_FIXTURE_ID=$probeId", "GRAPHBRIDGE_COMPOSE_PROJECT=$probeProject")
[IO.File]::WriteAllLines($probeEnv, $probeLines, [Text.UTF8Encoding]::new($false))
$probeCompose = @('compose', '--project-name', $probeProject, '-f', "$probeRepo/eng/docker-compose.yml", '-f', $probeOverride, '--env-file', $probeEnv)
try {
    if (-not $IsWindows) { chmod 600 $probeEnv; if ($LASTEXITCODE) { throw 'Credential permissions failed.' } }
    & docker @probeCompose up -d --pull never --wait --wait-timeout 180
    if ($LASTEXITCODE) { throw 'Probe fixture startup failed.' }
    "CREATE (:GraphBridgeFixture {id:'$probeId'});" | & docker @probeCompose exec -T neo4j sh -c 'exec cypher-shell -u neo4j -p "${NEO4J_AUTH#*/}"'
    if ($LASTEXITCODE) { throw 'Probe ownership marker failed.' }
    & docker @probeCompose exec -T neo4j sh -c 'exec cypher-shell -u neo4j -p "${NEO4J_AUTH#*/}" "CALL dbms.components() YIELD versions RETURN versions[0] AS version"' *> (Join-Path $probeOutput 'server.txt')
    & "$probeRepo/eng/scripts/Run-IntegrationTests.ps1" -NoBuild -Filter 'FullyQualifiedName~GraphRag' -Verbosity normal *> (Join-Path $probeOutput 'integration.log')
    & "$probeRepo/eng/scripts/Run-IntegrationTests.ps1" -NoBuild -Suite GraphRag -Verbosity normal *> (Join-Path $probeOutput 'graphrag.log')
    Get-Content (Join-Path $probeOutput 'server.txt'), (Join-Path $probeOutput 'integration.log'), (Join-Path $probeOutput 'graphrag.log') | Select-String 'Passed!|Failed!|Total tests|Passed:|Failed:|Skipped:|^\s*"?\d{4}\.'
}
finally {
    $probeCurrent = Get-FixtureComposeProjectFromEnvironmentFile -Path $probeEnv
    if ($probeCurrent -cne $probeProject) { throw 'Fixture ownership changed; refusing teardown.' }
    & "$probeRepo/eng/scripts/Stop-Neo4jFixture.ps1"
}
