# Shared, read-only preflight. Dot-source this file; it does not start or stop resources.

function Get-FixtureAvailableMemoryBytes {
    [CmdletBinding()]
    param()
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        try {
            $fixtureOperatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        }
        catch {
            try { $fixtureOperatingSystem = Get-WmiObject -Class Win32_OperatingSystem -ErrorAction Stop }
            catch { throw 'Cannot measure available Windows host memory; resource-heavy work is deferred.' }
        }
        $fixtureMemoryBytes = [long]$fixtureOperatingSystem.FreePhysicalMemory * 1KB
    }
    elseif (Test-Path -LiteralPath '/proc/meminfo') {
        try {
            $fixtureMemoryLine = Get-Content -LiteralPath '/proc/meminfo' -ErrorAction Stop |
                Where-Object { $_ -match '^MemAvailable:\s+(\d+)\s+kB$' }
            if (@($fixtureMemoryLine).Count -ne 1 -or $fixtureMemoryLine -notmatch '^MemAvailable:\s+(\d+)\s+kB$') {
                throw 'Missing MemAvailable.'
            }
            $fixtureMemoryBytes = [long]$Matches[1] * 1KB
        }
        catch { throw 'Cannot measure Linux host MemAvailable; resource-heavy work is deferred.' }
    }
    else { throw 'Host memory inspection is unsupported on this platform; resource-heavy work is deferred.' }
    if ($fixtureMemoryBytes -le 0) { throw 'Available host memory could not be measured reliably.' }
    return $fixtureMemoryBytes
}

function Get-FixtureResourceSnapshot {
    [CmdletBinding()]
    param()
    $fixtureMemoryBytes = Get-FixtureAvailableMemoryBytes
    try {
        $fixtureDockerLines = @(& docker ps --no-trunc --format '{{json .}}' 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Docker enumeration failed.' }
        $fixtureContainers = @()
        foreach ($fixtureDockerLine in $fixtureDockerLines) {
            $fixtureDockerRow = $fixtureDockerLine | ConvertFrom-Json -ErrorAction Stop
            if ($fixtureDockerRow.ID -notmatch '^[a-f0-9]{12,64}$') { throw 'Invalid Docker container identity.' }
            $fixtureLabels = @{}
            foreach ($fixtureLabel in ([string]$fixtureDockerRow.Labels).Split(',')) {
                $fixtureSeparator = $fixtureLabel.IndexOf('=')
                if ($fixtureSeparator -gt 0) {
                    $fixtureLabels[$fixtureLabel.Substring(0, $fixtureSeparator)] = $fixtureLabel.Substring($fixtureSeparator + 1)
                }
            }
            $fixtureContainers += [pscustomobject]@{
                Id = [string]$fixtureDockerRow.ID
                Project = $fixtureLabels['com.docker.compose.project']
                Service = $fixtureLabels['com.docker.compose.service']
                Disposable = $fixtureLabels['org.graphbridge.disposable']
            }
        }
    }
    catch { throw 'Cannot reliably enumerate running containers on the active Docker daemon; database work is deferred.' }
    return [pscustomobject]@{
        AvailableMemoryBytes = $fixtureMemoryBytes
        RunningContainers = $fixtureContainers
    }
}

function Get-FixtureComposeProjectFromEnvironmentFile {
    param([Parameter(Mandatory = $true)][string] $Path)
    $fixtureProjectLines = @([System.IO.File]::ReadAllLines($Path) | Where-Object { $_ -match '^GRAPHBRIDGE_COMPOSE_PROJECT=' })
    if ($fixtureProjectLines.Count -ne 1) { throw 'Generated fixture Compose ownership is missing or ambiguous.' }
    $fixtureProject = $fixtureProjectLines[0].Substring('GRAPHBRIDGE_COMPOSE_PROJECT='.Length)
    if ($fixtureProject -notmatch '^graphbridge-fixture-[a-f0-9]{32}$' -and $fixtureProject -ne 'graphbridge-disposable') {
        throw 'Generated fixture Compose ownership is outside the dedicated namespace.'
    }
    return $fixtureProject
}

function Test-FixtureDedicatedHostedRunner {
    param(
        [string] $Requested = $env:GRAPHBRIDGE_DEDICATED_HOSTED_RUNNER,
        [string] $GitHubActions = $env:GITHUB_ACTIONS,
        [string] $RunnerEnvironment = $env:RUNNER_ENVIRONMENT
    )
    if ($Requested -cne '1') { return $false }
    if ($GitHubActions -cne 'true' -or $RunnerEnvironment -cne 'github-hosted') {
        throw 'Dedicated runner policy requires an explicitly opted-in GitHub-hosted Actions job.'
    }
    return $true
}

function Assert-FixtureResources {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Startup', 'ExistingFixture')][string] $Mode,
        [string] $FixtureProject,
        [switch] $DedicatedHostedRunner = (Test-FixtureDedicatedHostedRunner),
        # Tests supply synthetic snapshots; production entry scripts never expose this argument.
        [scriptblock] $SnapshotProvider = { Get-FixtureResourceSnapshot }
    )
    if ($Mode -eq 'ExistingFixture' -and $FixtureProject -notmatch '^graphbridge-fixture-[a-f0-9]{32}$' -and $FixtureProject -ne 'graphbridge-disposable') {
        throw 'An existing-fixture check requires validated dedicated Compose ownership.'
    }
    $fixtureSnapshot = & $SnapshotProvider
    if ($null -eq $fixtureSnapshot -or $null -eq $fixtureSnapshot.AvailableMemoryBytes -or [long]$fixtureSnapshot.AvailableMemoryBytes -le 0 -or
        $null -eq $fixtureSnapshot.RunningContainers) {
        throw 'Incomplete resource inspection; database work is deferred.'
    }
    $fixtureContainers = @($fixtureSnapshot.RunningContainers)
    if ($Mode -eq 'ExistingFixture') {
        $fixtureOwnedContainers = @($fixtureContainers | Where-Object {
            $_.Project -ceq $FixtureProject -and $_.Service -ceq 'neo4j'
        })
        if ($fixtureOwnedContainers.Count -ne 1 -or $fixtureOwnedContainers[0].Disposable -cne 'true') {
            throw 'Database work refused: the selected Compose project must have exactly one owned disposable Neo4j service.'
        }
    }
    # Existing fixture memory is already reflected in host available memory.
    # Fresh hosted VMs have no second developer agent. Retain a 2 GiB OS/test reserve;
    # local and self-hosted machines always default to the shared 5 GiB reserve.
    $fixtureReserveBytes = 5GB
    if ($DedicatedHostedRunner) { $fixtureReserveBytes = 2GB }
    $fixtureRequiredBytes = $fixtureReserveBytes + 512MB
    if ($Mode -eq 'Startup') { $fixtureRequiredBytes += 1536MB }
    if ([long]$fixtureSnapshot.AvailableMemoryBytes -lt $fixtureRequiredBytes) {
        throw ('Database work deferred: {0:F2} GiB host RAM available; {1:F2} GiB required for a {2:F0} GiB reserve and bounded workload headroom.' -f
            ([long]$fixtureSnapshot.AvailableMemoryBytes / 1GB), ($fixtureRequiredBytes / 1GB), ($fixtureReserveBytes / 1GB))
    }
    Write-Verbose ('Resource preflight passed: {0:F2} GiB host RAM available; mode {1}.' -f
        ([long]$fixtureSnapshot.AvailableMemoryBytes / 1GB), $Mode)
}
