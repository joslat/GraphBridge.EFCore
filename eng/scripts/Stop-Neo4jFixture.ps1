[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixtureEngRoot = Split-Path -Parent $PSScriptRoot
$fixtureComposePath = Join-Path $fixtureEngRoot 'docker-compose.yml'
$fixtureEnvPath = Join-Path $fixtureEngRoot '.fixture.env'
if (-not (Test-Path -LiteralPath $fixtureEnvPath)) {
    throw 'No generated GraphBridge fixture environment exists; refusing to select any Docker project.'
}

$fixtureComposeProjectLine = [System.IO.File]::ReadAllLines($fixtureEnvPath) |
    Where-Object { $_ -match '^GRAPHBRIDGE_COMPOSE_PROJECT=' }
if (@($fixtureComposeProjectLine).Count -ne 1) { throw 'The generated fixture Compose project is missing or ambiguous.' }
$fixtureComposeProject = $fixtureComposeProjectLine.Substring('GRAPHBRIDGE_COMPOSE_PROJECT='.Length)
if ($fixtureComposeProject -notmatch '^graphbridge-fixture-[a-f0-9]{32}$' -and $fixtureComposeProject -ne 'graphbridge-disposable') {
    throw 'Refusing to stop a Docker project outside the GraphBridge disposable fixture namespace.'
}

& docker compose --project-name $fixtureComposeProject -f $fixtureComposePath --env-file $fixtureEnvPath down --volumes
if ($LASTEXITCODE -ne 0) { throw 'Fixture teardown failed; preserving the environment file for recovery.' }

# Verify the exact single file before deleting it. There is no recursive removal.
$fixtureResolvedEnv = (Resolve-Path -LiteralPath $fixtureEnvPath).Path
$fixtureExpectedEnv = [System.IO.Path]::GetFullPath((Join-Path $fixtureEngRoot '.fixture.env'))
if ($fixtureResolvedEnv -ne $fixtureExpectedEnv) { throw 'Unexpected fixture environment path.' }
# Unix dotfiles have the Hidden attribute in PowerShell; remove this validated file only.
Remove-Item -LiteralPath $fixtureResolvedEnv -Force
Write-Output 'Disposable GraphBridge fixture removed.'
