# Importing these definitions does not inspect Docker, launch a process, or modify environment values.
. (Join-Path $PSScriptRoot 'Assert-FixtureResources.ps1')
$benchmarkPinnedImage = 'neo4j:2026.09.0-community@sha256:6f3f17e72d69bb6f26b03f6186f5afb15a201f8b460bb69b0cd8030717f9f01a'

function ConvertFrom-BenchmarkFixtureEnvironment {
    param([Parameter(Mandatory = $true)][string[]] $Lines)
    $benchmarkValues = @{}
    foreach ($benchmarkLine in $Lines) {
        if ($benchmarkLine -cnotmatch '^(GRAPHBRIDGE_[A-Z0-9_]+)=(.*)$' -or $benchmarkValues.ContainsKey($Matches[1])) {
            throw 'Benchmark fixture environment contains an invalid or duplicate entry.'
        }
        $benchmarkValues[$Matches[1]] = $Matches[2]
    }
    $benchmarkKeys = @('GRAPHBRIDGE_INTEGRATION', 'GRAPHBRIDGE_NEO4J_URI', 'GRAPHBRIDGE_NEO4J_USERNAME',
        'GRAPHBRIDGE_NEO4J_PASSWORD', 'GRAPHBRIDGE_NEO4J_DATABASE', 'GRAPHBRIDGE_FIXTURE_ID', 'GRAPHBRIDGE_COMPOSE_PROJECT')
    if ($benchmarkValues.Count -ne $benchmarkKeys.Count -or
        @($benchmarkKeys | Where-Object { -not $benchmarkValues.ContainsKey($_) }).Count -ne 0 -or
        $benchmarkValues.GRAPHBRIDGE_INTEGRATION -cne '1' -or
        $benchmarkValues.GRAPHBRIDGE_NEO4J_URI -cne 'bolt://127.0.0.1:17687' -or
        $benchmarkValues.GRAPHBRIDGE_NEO4J_USERNAME -cne 'neo4j' -or
        $benchmarkValues.GRAPHBRIDGE_NEO4J_DATABASE -cne 'neo4j' -or
        $benchmarkValues.GRAPHBRIDGE_NEO4J_PASSWORD -cnotmatch '^[A-Fa-f0-9]{64}$' -or
        $benchmarkValues.GRAPHBRIDGE_FIXTURE_ID -cnotmatch '^[a-f0-9]{32}$' -or
        $benchmarkValues.GRAPHBRIDGE_COMPOSE_PROJECT -cne ('graphbridge-fixture-' + $benchmarkValues.GRAPHBRIDGE_FIXTURE_ID)) {
        throw 'Benchmark fixture environment does not match the exact generated Community contract.'
    }
    return $benchmarkValues
}

function Assert-BenchmarkMatrix {
    param([int[]] $Sizes, [int[]] $EdgeSizes, [int[]] $Writes, [int] $Samples, [int] $Warmups, [int] $FetchSize, [int] $Seed, [int] $MaximumMinutes)
    if ($Sizes.Count -eq 0 -or $EdgeSizes.Count -eq 0 -or $Writes.Count -eq 0 -or
        @($Sizes | Select-Object -Unique).Count -ne $Sizes.Count -or @($Writes | Select-Object -Unique).Count -ne $Writes.Count -or
        @($EdgeSizes | Select-Object -Unique).Count -ne $EdgeSizes.Count -or
        @($Sizes | Where-Object { $_ -notin @(1, 100, 10000, 100000) }).Count -ne 0 -or
        @($EdgeSizes | Where-Object { $_ -notin @(1, 100, 10000) }).Count -ne 0 -or
        @($Writes | Where-Object { $_ -notin @(1, 100, 1000) }).Count -ne 0 -or
        $Samples -lt 2 -or $Samples -gt 200 -or $Warmups -lt 0 -or $Warmups -gt 20 -or
        $FetchSize -lt 1 -or $FetchSize -gt 10000 -or $Seed -lt 0 -or $MaximumMinutes -lt 1 -or $MaximumMinutes -gt 60) {
        throw 'Benchmark matrix or duration exceeds the reviewed finite bounds.'
    }
}

function Assert-BenchmarkCompletion {
    param([object] $Completion, [object] $EdgeCompletion, [int[]] $Sizes, [int[]] $EdgeSizes, [int[]] $Writes)
    # Completion is separate for node and edge workloads; a valid node-only file cannot close a combined run.
    if ($Completion.Completed -isnot [bool] -or $EdgeCompletion.Completed -isnot [bool] -or
        $Completion.Completed -cne $true -or $EdgeCompletion.Completed -cne $true -or
        ($Completion.Nodes.GraphSizes -join ',') -cne ($Sizes -join ',') -or
        ($Completion.Nodes.WriteSizes -join ',') -cne ($Writes -join ',') -or
        ($Completion.Edges.EdgeCounts -join ',') -cne ($EdgeSizes -join ',') -or
        ($EdgeCompletion.EdgeCounts -join ',') -cne ($EdgeSizes -join ',') -or
        $Completion.Nodes.ReadCases -ne (8 * $Sizes.Count) -or
        $Completion.Nodes.WriteCases -ne (6 * $Sizes.Count * $Writes.Count) -or
        $Completion.Nodes.ProtocolCases -ne ($Sizes.Count * (8 + 6 * $Writes.Count)) -or
        $Completion.Nodes.ExplainCases -ne (13 * $Sizes.Count) -or $Completion.Nodes.ProfileCases -ne (13 * $Sizes.Count) -or
        $Completion.Edges.ReadCases -ne (8 * $EdgeSizes.Count) -or $Completion.Edges.ProtocolCases -ne (8 * $EdgeSizes.Count) -or
        $Completion.Edges.ExplainAndProfilePlans -ne (16 * $EdgeSizes.Count) -or
        $EdgeCompletion.ReadCases -ne (8 * $EdgeSizes.Count) -or $EdgeCompletion.ProtocolCases -ne (8 * $EdgeSizes.Count) -or
        $EdgeCompletion.ExplainAndProfilePlans -ne (16 * $EdgeSizes.Count)) {
        throw 'Benchmark completion does not cover the selected node and edge matrices with verified cleanup.'
    }
}

function Get-BenchmarkContainerMetadata {
    param([Parameter(Mandatory = $true)][string] $ContainerId)
    if ($ContainerId -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid selected benchmark container ID.' }
    # Explicit allowlist: never inspect/serialize Config.Env, mounts containing paths, or auth credentials.
    $benchmarkFormat = '{"Id":{{json .Id}},"Image":{{json .Config.Image}},"Project":{{json (index .Config.Labels "com.docker.compose.project")}},"Service":{{json (index .Config.Labels "com.docker.compose.service")}},"Disposable":{{json (index .Config.Labels "org.graphbridge.disposable")}},"Memory":{{json .HostConfig.Memory}},"MemorySwap":{{json .HostConfig.MemorySwap}},"NanoCpus":{{json .HostConfig.NanoCpus}},"Ports":{{json .HostConfig.PortBindings}},"Tmpfs":{{json .HostConfig.Tmpfs}},"Running":{{json .State.Running}},"Health":{{json .State.Health.Status}}}'
    $benchmarkInspection = @(& docker inspect --format $benchmarkFormat $ContainerId)
    if ($LASTEXITCODE -ne 0 -or $benchmarkInspection.Count -ne 1) { throw 'Cannot inspect selected benchmark container safely.' }
    try { return $benchmarkInspection[0] | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Selected benchmark container returned malformed safe metadata.' }
}

function Assert-BenchmarkContainer {
    param([Parameter(Mandatory = $true)][object] $Metadata, [Parameter(Mandatory = $true)][hashtable] $Fixture,
        [Parameter(Mandatory = $true)][string] $ContainerId, [switch] $ForCleanup)
    if ($Metadata.Id -cne $ContainerId -or $Metadata.Image -cne $benchmarkPinnedImage -or
        $Metadata.Project -cne $Fixture.GRAPHBRIDGE_COMPOSE_PROJECT -or $Metadata.Service -cne 'neo4j' -or
        $Metadata.Disposable -cne 'true' -or (-not $ForCleanup -and ($Metadata.Running -ne $true -or $Metadata.Health -cne 'healthy')) -or
        $Metadata.Memory -ne 1536MB -or $Metadata.NanoCpus -ne 1000000000) {
        throw 'Selected benchmark container identity, state, image or limits do not match the pinned fixture.'
    }
    if ($null -eq $Metadata.Ports -or @($Metadata.Ports.PSObject.Properties).Count -ne 2) {
        throw 'Benchmark ports must be exactly the two reviewed loopback bindings.'
    }
    foreach ($benchmarkBinding in @(@('7687/tcp', '17687'), @('7474/tcp', '17474'))) {
        $benchmarkPorts = @($Metadata.Ports.($benchmarkBinding[0]))
        if ($benchmarkPorts.Count -ne 1 -or $benchmarkPorts[0].HostIp -cne '127.0.0.1' -or $benchmarkPorts[0].HostPort -cne $benchmarkBinding[1]) {
            throw 'Benchmark ports must be exactly the two reviewed loopback bindings.'
        }
    }
    if ($null -eq $Metadata.Tmpfs -or @($Metadata.Tmpfs.PSObject.Properties).Count -ne 2 -or
        $Metadata.Tmpfs.'/data' -cne 'uid=7474,gid=7474,mode=0700' -or $Metadata.Tmpfs.'/logs' -cne 'uid=7474,gid=7474,mode=0700') {
        throw 'Benchmark fixture must use the reviewed disposable data/log tmpfs mounts.'
    }
}

function New-BenchmarkChildEnvironment {
    param([Parameter(Mandatory = $true)][hashtable] $Inherited, [Parameter(Mandatory = $true)][hashtable] $Fixture,
        [Parameter(Mandatory = $true)][hashtable] $Provenance)
    # Only the child receives overrides. The launching PowerShell process is never changed.
    $benchmarkChildValues = $Inherited.Clone()
    foreach ($benchmarkName in @($benchmarkChildValues.Keys)) {
        if ([string]$benchmarkName -like 'GRAPHBRIDGE_BENCHMARK*') { $benchmarkChildValues.Remove($benchmarkName) }
    }
    foreach ($benchmarkName in $Fixture.Keys) { $benchmarkChildValues[$benchmarkName] = $Fixture[$benchmarkName] }
    foreach ($benchmarkName in $Provenance.Keys) { $benchmarkChildValues[$benchmarkName] = $Provenance[$benchmarkName] }
    $benchmarkChildValues.GRAPHBRIDGE_BENCHMARKS = '1'
    $benchmarkChildValues.GRAPHBRIDGE_BENCHMARK_GUARDED = '1'
    return $benchmarkChildValues
}

function Wait-BenchmarkProcess {
    param([Parameter(Mandatory = $true)][object] $Child,
        [Parameter(Mandatory = $true)][long] $ReserveBytes,
        [Parameter(Mandatory = $true)][int] $MaximumSeconds,
        [long] $HeadroomBytes = 256MB,
        # Pure policy tests supply fake process/clock/memory; the entry script exposes none of these seams.
        [scriptblock] $AvailableMemory = { Get-FixtureAvailableMemoryBytes },
        [scriptblock] $ElapsedSeconds,
        [scriptblock] $OnSample = { param($Elapsed, $Available) })
    if ($ReserveBytes -le 0 -or $MaximumSeconds -le 0 -or $HeadroomBytes -lt 0) { throw 'Invalid benchmark supervision limits.' }
    $benchmarkWatch = [Diagnostics.Stopwatch]::StartNew()
    $benchmarkFailed = $false
    try {
        while (-not $Child.WaitForExit(5000)) {
            $benchmarkElapsed = if ($null -ne $ElapsedSeconds) { & $ElapsedSeconds } else { $benchmarkWatch.Elapsed.TotalSeconds }
            $benchmarkAvailable = & $AvailableMemory
            if ($null -eq $benchmarkAvailable -or [long]$benchmarkAvailable -le 0) { throw 'Benchmark memory measurement failed; stopping the owned workload.' }
            & $OnSample $benchmarkElapsed $benchmarkAvailable
            if ([long]$benchmarkAvailable -lt ($ReserveBytes + $HeadroomBytes)) { throw 'Benchmark stopped: available host memory approached the required reserve and headroom.' }
            if ([double]$benchmarkElapsed -ge $MaximumSeconds) { throw 'Benchmark stopped: configured duration limit reached.' }
        }
        $Child.Refresh()
        if ($Child.ExitCode -ne 0) { throw ('Benchmark child failed with exit code ' + $Child.ExitCode + '; no acceptance claimed.') }
    }
    catch { $benchmarkFailed = $true; throw }
    finally {
        try {
            if (-not $Child.HasExited) {
                $Child.Kill($true)
                if (-not $Child.WaitForExit(30000)) { throw 'Owned benchmark child did not terminate within the cleanup deadline.' }
            }
        }
        catch {
            if (-not $benchmarkFailed) { throw }
            Write-Warning 'Owned benchmark child cleanup could not be confirmed; preserve the original failure and inspect its PID.'
        }
    }
}
