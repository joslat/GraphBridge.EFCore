#requires -Version 7.0
# Runs CompatibilityReadProbeTests against the reviewed Neo4j 5.26 LTS image in its own owned fixture, then removes it.
# The image must already be cached (docker pull it by the digest below); the probe never pulls implicitly.
# Evidence goes to .artifacts/next-roadmap/profile-probe.
$ErrorActionPreference = 'Stop'
$probeRepo = (Resolve-Path "$PSScriptRoot/../..").Path
Set-Location $probeRepo
. "$probeRepo/eng/scripts/Assert-FixtureResources.ps1"
$probeEnv = Join-Path $probeRepo 'eng/.fixture.env'
if (Test-Path -LiteralPath $probeEnv) { throw 'Complete the existing fixture workload before this alternate profile.' }
if ((Get-FixtureAvailableMemoryBytes) -lt 6GB) { throw 'Insufficient build headroom.' }
$probeOutput = Join-Path $probeRepo '.artifacts/next-roadmap/profile-probe'
[IO.Directory]::CreateDirectory($probeOutput) | Out-Null
dotnet build tests/GraphBridge.Tooling.IntegrationTests/GraphBridge.Tooling.IntegrationTests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false *> (Join-Path $probeOutput 'build.log')
if ($LASTEXITCODE) { throw "Probe build failed; see $probeOutput/build.log" }
Assert-FixtureResources -Mode Startup
$probeImage = 'neo4j@sha256:ef6f1cb1a5594cbfddb82876a9dc67171941eabca3a38100f2bef0b3fc6c05e9'
$probeIdentity = docker image inspect $probeImage --format '{{.Id}} {{json .RepoDigests}}'
if ($LASTEXITCODE) { throw 'Reviewed cached image missing; no implicit image pull.' }
$probeIdentity | Set-Content (Join-Path $probeOutput 'image.txt') -Encoding utf8
$probeOverride = Join-Path $probeOutput 'compose.override.yml'
[IO.File]::WriteAllText($probeOverride, "services:`n  neo4j:`n    image: $probeImage`n", [Text.UTF8Encoding]::new($false))
$probeId = [Guid]::NewGuid().ToString('N')
$probeProject = 'graphbridge-fixture-' + $probeId
$probePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$probeLines = @('GRAPHBRIDGE_INTEGRATION=1', 'GRAPHBRIDGE_NEO4J_URI=bolt://127.0.0.1:17687',
    'GRAPHBRIDGE_NEO4J_USERNAME=neo4j', "GRAPHBRIDGE_NEO4J_PASSWORD=$probePassword",
    'GRAPHBRIDGE_NEO4J_DATABASE=neo4j', "GRAPHBRIDGE_FIXTURE_ID=$probeId", "GRAPHBRIDGE_COMPOSE_PROJECT=$probeProject")
[IO.File]::WriteAllLines($probeEnv, $probeLines, [Text.UTF8Encoding]::new($false))
$probeCompose = @('compose', '--project-name', $probeProject, '-f', "$probeRepo/eng/docker-compose.yml", '-f', $probeOverride, '--env-file', $probeEnv)
$probePreviousVersion = $env:GRAPHBRIDGE_PROBE_VERSION
try {
    if (-not $IsWindows) { chmod 600 $probeEnv; if ($LASTEXITCODE) { throw 'Credential permissions failed.' } }
    & docker @probeCompose up -d --pull never --wait --wait-timeout 180
    if ($LASTEXITCODE) { throw 'Profile startup failed.' }
    "CREATE (:GraphBridgeFixture {id:'$probeId'});" | & docker @probeCompose exec -T neo4j sh -c 'exec cypher-shell -u neo4j -p "${NEO4J_AUTH#*/}"'
    if ($LASTEXITCODE) { throw 'Profile ownership marker failed.' }
    $env:GRAPHBRIDGE_PROBE_VERSION = '5.26.18'
    & "$probeRepo/eng/scripts/Run-IntegrationTests.ps1" -Suite Tooling -NoBuild -Filter 'FullyQualifiedName~CompatibilityReadProbeTests' *> (Join-Path $probeOutput 'live.log')
    Get-Content (Join-Path $probeOutput 'live.log')
}
finally {
    $env:GRAPHBRIDGE_PROBE_VERSION = $probePreviousVersion
    $probeCurrent = Get-FixtureComposeProjectFromEnvironmentFile -Path $probeEnv
    if ($probeCurrent -cne $probeProject) { throw 'Fixture ownership changed; refusing teardown.' }
    & "$probeRepo/eng/scripts/Stop-Neo4jFixture.ps1"
}
