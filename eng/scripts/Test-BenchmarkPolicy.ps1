[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../eng/scripts/Benchmark.Common.ps1')
$benchmarkPolicyPasses = 0
function docker { throw 'Offline benchmark policy checks must never call Docker.' }
function Start-Process { throw 'Offline benchmark policy checks must never launch a process.' }
function Test-BenchmarkCase {
    param([string] $Name, [scriptblock] $Body, [string] $Failure)
    $benchmarkError = $null
    try { & $Body | Out-Null } catch { $benchmarkError = $_.Exception.Message }
    if ([string]::IsNullOrEmpty($Failure)) {
        if ($null -ne $benchmarkError) { throw ('Benchmark policy failed: ' + $Name + ': ' + $benchmarkError) }
    }
    elseif ($null -eq $benchmarkError -or $benchmarkError.IndexOf($Failure, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw ('Benchmark policy failed for an unexpected reason: ' + $Name)
    }
    $script:benchmarkPolicyPasses++
    Write-Output ('PASS: ' + $Name)
}
function New-BenchmarkFakeChild {
    param([int] $CompleteAt = 2, [int] $ExitCode = 0)
    $benchmarkFake = [pscustomobject]@{ HasExited = $false; ExitCode = $ExitCode; CompleteAt = $CompleteAt; Ticks = 0; Killed = $false; EntireTree = $false }
    $benchmarkFake | Add-Member ScriptMethod WaitForExit {
        param($Milliseconds)
        if ($this.HasExited) { return $true }
        $this.Ticks++
        if ($this.CompleteAt -gt 0 -and $this.Ticks -ge $this.CompleteAt) { $this.HasExited = $true }
        return $this.HasExited
    }
    $benchmarkFake | Add-Member ScriptMethod Refresh { }
    $benchmarkFake | Add-Member ScriptMethod Kill { param($EntireTree) $this.Killed = $true; $this.EntireTree = $EntireTree; $this.HasExited = $true }
    return $benchmarkFake
}
$benchmarkPolicyFixture = @{
    GRAPHBRIDGE_INTEGRATION = '1'; GRAPHBRIDGE_NEO4J_URI = 'bolt://127.0.0.1:17687'
    GRAPHBRIDGE_NEO4J_USERNAME = 'neo4j'; GRAPHBRIDGE_NEO4J_PASSWORD = ('A' * 64)
    GRAPHBRIDGE_NEO4J_DATABASE = 'neo4j'; GRAPHBRIDGE_FIXTURE_ID = ('a' * 32)
    GRAPHBRIDGE_COMPOSE_PROJECT = ('graphbridge-fixture-' + ('a' * 32))
}
function ConvertTo-BenchmarkPolicyLines {
    param([hashtable] $Values)
    return @($Values.Keys | Sort-Object | ForEach-Object { $_ + '=' + $Values[$_] })
}
Test-BenchmarkCase 'Exact generated Community environment is accepted' {
    ConvertFrom-BenchmarkFixtureEnvironment -Lines (ConvertTo-BenchmarkPolicyLines $benchmarkPolicyFixture)
}
foreach ($benchmarkFault in @('missing-optin', 'zero-optin', 'wrong-port', 'remote-uri', 'wrong-project', 'wrong-user', 'weak-secret', 'unknown-key', 'duplicate-key')) {
    $benchmarkBroken = $benchmarkPolicyFixture.Clone()
    switch ($benchmarkFault) {
        'missing-optin' { $benchmarkBroken.Remove('GRAPHBRIDGE_INTEGRATION') }
        'zero-optin' { $benchmarkBroken.GRAPHBRIDGE_INTEGRATION = '0' }
        'wrong-port' { $benchmarkBroken.GRAPHBRIDGE_NEO4J_URI = 'bolt://127.0.0.1:18687' }
        'remote-uri' { $benchmarkBroken.GRAPHBRIDGE_NEO4J_URI = 'bolt://production:7687' }
        'wrong-project' { $benchmarkBroken.GRAPHBRIDGE_COMPOSE_PROJECT = 'other-developer' }
        'wrong-user' { $benchmarkBroken.GRAPHBRIDGE_NEO4J_USERNAME = 'other-user' }
        'weak-secret' { $benchmarkBroken.GRAPHBRIDGE_NEO4J_PASSWORD = 'not-generated' }
        'unknown-key' { $benchmarkBroken.GRAPHBRIDGE_UNREVIEWED = '1' }
    }
    $benchmarkBrokenLines = ConvertTo-BenchmarkPolicyLines $benchmarkBroken
    if ($benchmarkFault -eq 'duplicate-key') { $benchmarkBrokenLines += 'GRAPHBRIDGE_INTEGRATION=1' }
    Test-BenchmarkCase ('Fixture rejection: ' + $benchmarkFault) {
        ConvertFrom-BenchmarkFixtureEnvironment -Lines $benchmarkBrokenLines
    } 'Benchmark fixture environment'
}

$benchmarkPolicyMatrix = @{ Sizes = @(1, 100, 10000, 100000); EdgeSizes = @(1, 100, 10000); Writes = @(1, 100, 1000); Samples = 20; Warmups = 3; FetchSize = 1000; Seed = 1729; MaximumMinutes = 60 }
Test-BenchmarkCase 'Full matrix remains available at the time limit' { Assert-BenchmarkMatrix @benchmarkPolicyMatrix }
Test-BenchmarkCase 'Single-node graph supports the complete bounded write matrix' {
    $benchmarkSingleNodeMatrix = $benchmarkPolicyMatrix.Clone()
    $benchmarkSingleNodeMatrix.Sizes = @(1)
    Assert-BenchmarkMatrix @benchmarkSingleNodeMatrix
}
Test-BenchmarkCase 'Smoke selects one and one hundred edges independently of node size' {
    $benchmarkSmokeMatrix = $benchmarkPolicyMatrix.Clone()
    $benchmarkSmokeMatrix.Sizes = @(100)
    $benchmarkSmokeMatrix.EdgeSizes = @(1, 100)
    $benchmarkSmokeMatrix.Writes = @(1)
    $benchmarkSmokeMatrix.Samples = 2
    $benchmarkSmokeMatrix.Warmups = 0
    Assert-BenchmarkMatrix @benchmarkSmokeMatrix
}
foreach ($benchmarkMatrixFault in @('duplicate-size', 'zero-size', 'duplicate-single-node', 'oversize', 'empty-edges', 'zero-edges', 'duplicate-edges', 'oversized-edges', 'oversized-write', 'few-samples', 'many-samples', 'warmups', 'fetch', 'duration', 'negative-seed')) {
    $benchmarkBrokenMatrix = $benchmarkPolicyMatrix.Clone()
    switch ($benchmarkMatrixFault) {
        'duplicate-size' { $benchmarkBrokenMatrix.Sizes = @(100, 100) }
        'zero-size' { $benchmarkBrokenMatrix.Sizes = @(0) }
        'duplicate-single-node' { $benchmarkBrokenMatrix.Sizes = @(1, 1) }
        'oversize' { $benchmarkBrokenMatrix.Sizes = @(1000000) }
        'empty-edges' { $benchmarkBrokenMatrix.EdgeSizes = @() }
        'zero-edges' { $benchmarkBrokenMatrix.EdgeSizes = @(0) }
        'duplicate-edges' { $benchmarkBrokenMatrix.EdgeSizes = @(1, 1) }
        'oversized-edges' { $benchmarkBrokenMatrix.EdgeSizes = @(100000) }
        'oversized-write' { $benchmarkBrokenMatrix.Writes = @(10000) }
        'few-samples' { $benchmarkBrokenMatrix.Samples = 1 }
        'many-samples' { $benchmarkBrokenMatrix.Samples = 201 }
        'warmups' { $benchmarkBrokenMatrix.Warmups = 21 }
        'fetch' { $benchmarkBrokenMatrix.FetchSize = 0 }
        'duration' { $benchmarkBrokenMatrix.MaximumMinutes = 61 }
        'negative-seed' { $benchmarkBrokenMatrix.Seed = -1 }
    }
    Test-BenchmarkCase ('Finite matrix rejection: ' + $benchmarkMatrixFault) { Assert-BenchmarkMatrix @benchmarkBrokenMatrix } 'finite bounds'
}

$benchmarkPolicyCompletion = @{
    Completed = $true
    Nodes = @{ GraphSizes = @(100); WriteSizes = @(1); ReadCases = 8; WriteCases = 6; ProtocolCases = 14; ExplainCases = 13; ProfileCases = 13 }
    Edges = @{ EdgeCounts = @(1, 100); ReadCases = 16; ProtocolCases = 16; ExplainAndProfilePlans = 32 }
}
$benchmarkPolicyEdgeCompletion = @{ Completed = $true; EdgeCounts = @(1, 100); ReadCases = 16; ProtocolCases = 16; ExplainAndProfilePlans = 32 }
Test-BenchmarkCase 'Completion requires matching separate node and edge coverage' {
    Assert-BenchmarkCompletion -Completion $benchmarkPolicyCompletion -EdgeCompletion $benchmarkPolicyEdgeCompletion -Sizes @(100) -EdgeSizes @(1, 100) -Writes @(1)
}
foreach ($benchmarkCompletionFault in @('node-only', 'edge-incomplete', 'string-success', 'wrong-node-size', 'wrong-write-size', 'wrong-edge-size', 'missing-node-plan', 'missing-edge-plan', 'missing-edge-protocol', 'secondary-mismatch')) {
    $benchmarkBrokenCompletion = $benchmarkPolicyCompletion | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    $benchmarkBrokenEdgeCompletion = $benchmarkPolicyEdgeCompletion | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    switch ($benchmarkCompletionFault) {
        'node-only' { $benchmarkBrokenCompletion.PSObject.Properties.Remove('Edges') }
        'edge-incomplete' { $benchmarkBrokenEdgeCompletion.Completed = $false }
        'string-success' { $benchmarkBrokenCompletion.Completed = 'true' }
        'wrong-node-size' { $benchmarkBrokenCompletion.Nodes.GraphSizes = @(1) }
        'wrong-write-size' { $benchmarkBrokenCompletion.Nodes.WriteSizes = @(100) }
        'wrong-edge-size' { $benchmarkBrokenCompletion.Edges.EdgeCounts = @(1, 10000) }
        'missing-node-plan' { $benchmarkBrokenCompletion.Nodes.ExplainCases = 12 }
        'missing-edge-plan' { $benchmarkBrokenCompletion.Edges.ExplainAndProfilePlans = 31 }
        'missing-edge-protocol' { $benchmarkBrokenCompletion.Edges.ProtocolCases = 15 }
        'secondary-mismatch' { $benchmarkBrokenEdgeCompletion.EdgeCounts = @(1) }
    }
    Test-BenchmarkCase ('Completion rejection: ' + $benchmarkCompletionFault) {
        Assert-BenchmarkCompletion -Completion $benchmarkBrokenCompletion -EdgeCompletion $benchmarkBrokenEdgeCompletion -Sizes @(100) -EdgeSizes @(1, 100) -Writes @(1)
    } 'Benchmark completion'
}

$benchmarkPolicyMetadata = [pscustomobject]@{
    Id = ('b' * 64); Image = $benchmarkPinnedImage; Project = $benchmarkPolicyFixture.GRAPHBRIDGE_COMPOSE_PROJECT
    Service = 'neo4j'; Disposable = 'true'; Memory = 1536MB; MemorySwap = 3GB; NanoCpus = 1000000000
    Running = $true; Health = 'healthy'
    Ports = [pscustomobject]@{ '7687/tcp' = @([pscustomobject]@{ HostIp = '127.0.0.1'; HostPort = '17687' }); '7474/tcp' = @([pscustomobject]@{ HostIp = '127.0.0.1'; HostPort = '17474' }) }
    Tmpfs = [pscustomobject]@{ '/data' = 'uid=7474,gid=7474,mode=0700'; '/logs' = 'uid=7474,gid=7474,mode=0700' }
}
Test-BenchmarkCase 'Exact safe container metadata passes' {
    Assert-BenchmarkContainer -Metadata $benchmarkPolicyMetadata -Fixture $benchmarkPolicyFixture -ContainerId ('b' * 64)
}
foreach ($benchmarkMetadataFault in @('image', 'project', 'service', 'label', 'memory', 'cpu', 'health', 'wildcard-bind', 'additional-bind', 'port', 'durable-data')) {
    $benchmarkBrokenMetadata = $benchmarkPolicyMetadata | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    switch ($benchmarkMetadataFault) {
        'image' { $benchmarkBrokenMetadata.Image = 'neo4j:latest' }
        'project' { $benchmarkBrokenMetadata.Project = 'other-repository' }
        'service' { $benchmarkBrokenMetadata.Service = 'other-service' }
        'label' { $benchmarkBrokenMetadata.Disposable = 'false' }
        'memory' { $benchmarkBrokenMetadata.Memory = 0 }
        'cpu' { $benchmarkBrokenMetadata.NanoCpus = 0 }
        'health' { $benchmarkBrokenMetadata.Health = 'unhealthy' }
        'wildcard-bind' { $benchmarkBrokenMetadata.Ports.'7687/tcp'[0].HostIp = '0.0.0.0' }
        'additional-bind' { $benchmarkBrokenMetadata.Ports.'7687/tcp' += [pscustomobject]@{ HostIp = '::'; HostPort = '17687' } }
        'port' { $benchmarkBrokenMetadata.Ports.'7687/tcp'[0].HostPort = '7687' }
        'durable-data' { $benchmarkBrokenMetadata.Tmpfs = $null }
    }
    Test-BenchmarkCase ('Container rejection: ' + $benchmarkMetadataFault) {
        Assert-BenchmarkContainer -Metadata $benchmarkBrokenMetadata -Fixture $benchmarkPolicyFixture -ContainerId ('b' * 64)
    } $(if ($benchmarkMetadataFault -in @('wildcard-bind', 'additional-bind', 'port')) { 'Benchmark ports' } elseif ($benchmarkMetadataFault -eq 'durable-data') { 'disposable data/log' } else { 'pinned fixture' })
}
Test-BenchmarkCase 'Cleanup can select its unhealthy owned server but never a foreign project' {
    $benchmarkUnhealthy = $benchmarkPolicyMetadata | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    $benchmarkUnhealthy.Health = 'unhealthy'
    Assert-BenchmarkContainer -Metadata $benchmarkUnhealthy -Fixture $benchmarkPolicyFixture -ContainerId ('b' * 64) -ForCleanup
    $benchmarkUnhealthy.Project = 'foreign'
    try { Assert-BenchmarkContainer -Metadata $benchmarkUnhealthy -Fixture $benchmarkPolicyFixture -ContainerId ('b' * 64) -ForCleanup }
    catch { if ($_.Exception.Message -like '*pinned fixture*') { return }; throw }
    throw 'Cleanup accepted a foreign project.'
}

Test-BenchmarkCase 'Child environment overrides stale credentials/URI and leaves parent dictionary untouched' {
    $benchmarkInherited = @{ GRAPHBRIDGE_NEO4J_URI = 'bolt://elsewhere'; GRAPHBRIDGE_NEO4J_PASSWORD = 'stale'; GRAPHBRIDGE_BENCHMARKS = '0'; GRAPHBRIDGE_BENCHMARK_UNREVIEWED = '1'; PATH = 'retained' }
    $benchmarkOverlay = New-BenchmarkChildEnvironment -Inherited $benchmarkInherited -Fixture $benchmarkPolicyFixture -Provenance @{ GRAPHBRIDGE_BENCHMARK_COMMIT = ('c' * 40) }
    if ($benchmarkOverlay.GRAPHBRIDGE_NEO4J_URI -cne $benchmarkPolicyFixture.GRAPHBRIDGE_NEO4J_URI -or
        $benchmarkOverlay.GRAPHBRIDGE_NEO4J_PASSWORD -cne $benchmarkPolicyFixture.GRAPHBRIDGE_NEO4J_PASSWORD -or
        $benchmarkOverlay.GRAPHBRIDGE_BENCHMARKS -cne '1' -or $benchmarkOverlay.GRAPHBRIDGE_BENCHMARK_GUARDED -cne '1' -or
        $benchmarkOverlay.ContainsKey('GRAPHBRIDGE_BENCHMARK_UNREVIEWED') -or $benchmarkOverlay.PATH -cne 'retained' -or
        $benchmarkInherited.GRAPHBRIDGE_NEO4J_PASSWORD -cne 'stale' -or $benchmarkInherited.GRAPHBRIDGE_BENCHMARKS -cne '0') {
        throw 'Child-only environment isolation failed.'
    }
}

Test-BenchmarkCase 'Normal completion at exact local reserve plus monitoring headroom does not kill the child' {
    $benchmarkFake = New-BenchmarkFakeChild
    Wait-BenchmarkProcess -Child $benchmarkFake -ReserveBytes 5GB -MaximumSeconds 60 -AvailableMemory { 5GB + 256MB } -ElapsedSeconds { 1 }
    if ($benchmarkFake.Killed) { throw 'Successful child was killed.' }
}
Test-BenchmarkCase 'Hosted reserve plus monitoring headroom is retained' {
    $benchmarkFake = New-BenchmarkFakeChild
    Wait-BenchmarkProcess -Child $benchmarkFake -ReserveBytes 2GB -MaximumSeconds 60 -AvailableMemory { 2GB + 256MB } -ElapsedSeconds { 1 }
}
foreach ($benchmarkSupervisionFault in @('low-memory', 'unknown-memory', 'measurement-exception', 'timeout')) {
    $benchmarkFake = New-BenchmarkFakeChild -CompleteAt 0
    $benchmarkMemory = switch ($benchmarkSupervisionFault) {
        'low-memory' { { 5GB + 256MB - 1 } }
        'unknown-memory' { { $null } }
        'measurement-exception' { { throw 'Synthetic memory inspector failed' } }
        default { { 6GB } }
    }
    $benchmarkExpectedFailure = switch ($benchmarkSupervisionFault) {
        'low-memory' { 'approached the required reserve' }
        'unknown-memory' { 'memory measurement failed' }
        'measurement-exception' { 'Synthetic memory inspector failed' }
        default { 'duration limit reached' }
    }
    Test-BenchmarkCase ('Supervision failure kills only its passed process tree: ' + $benchmarkSupervisionFault) {
        try { Wait-BenchmarkProcess -Child $benchmarkFake -ReserveBytes 5GB -MaximumSeconds 60 -AvailableMemory $benchmarkMemory -ElapsedSeconds { 60 } }
        finally { if (-not $benchmarkFake.Killed -or -not $benchmarkFake.EntireTree -or -not $benchmarkFake.HasExited) { throw 'Owned child tree cleanup was skipped.' } }
    } $benchmarkExpectedFailure
}
Test-BenchmarkCase 'Nonzero child exit is not acceptance' {
    Wait-BenchmarkProcess -Child (New-BenchmarkFakeChild -CompleteAt 1 -ExitCode 7) -ReserveBytes 5GB -MaximumSeconds 60
} 'exit code 7'
Write-Output "$benchmarkPolicyPasses offline benchmark policy assertions passed; synthetic metadata/memory and fake child objects only."
