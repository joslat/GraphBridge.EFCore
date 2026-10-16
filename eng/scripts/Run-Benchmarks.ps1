[CmdletBinding()]
param(
    [string] $Sizes = '1,100,10000,100000',
    [string] $EdgeSizes = '1,100,10000',
    [string] $Writes = '1,100,1000',
    [int] $Samples = 20,
    [int] $Warmups = 3,
    [int] $FetchSize = 1000,
    [int] $Seed = 1729,
    [int] $TimeoutMinutes = 30,
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [ValidateSet('', 'driver', 'ef')][string] $AllocationBackend = '',
    [switch] $Bulk,
    [string] $TraceToolPath = ''
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Benchmarks require PowerShell 7+ for ArgumentList and owned process-tree cleanup.' }
. (Join-Path $PSScriptRoot '../../eng/scripts/Benchmark.Common.ps1')
if ($Sizes -cnotmatch '^\d+(,\d+)*$' -or $EdgeSizes -cnotmatch '^\d+(,\d+)*$' -or $Writes -cnotmatch '^\d+(,\d+)*$') { throw 'Sizes, EdgeSizes and Writes must be comma-separated integer lists.' }
$benchmarkSizes = [int[]]$Sizes.Split(',')
$benchmarkEdgeSizes = [int[]]$EdgeSizes.Split(',')
$benchmarkWrites = [int[]]$Writes.Split(',')
Assert-BenchmarkMatrix -Sizes $benchmarkSizes -EdgeSizes $benchmarkEdgeSizes -Writes $benchmarkWrites -Samples $Samples -Warmups $Warmups -FetchSize $FetchSize -Seed $Seed -MaximumMinutes $TimeoutMinutes
if ($AllocationBackend -and ($Sizes -cne '100000' -or $EdgeSizes -cne '1' -or $Writes -cne '1')) { throw 'Focused allocations require Sizes=100000, EdgeSizes=1 and Writes=1; edge/write placeholders are not executed.' }
if ($Bulk -and ($AllocationBackend -or $Sizes -cne '1' -or $EdgeSizes -cne '1')) { throw 'Bulk node profiling requires Sizes=1 and EdgeSizes=1 placeholders, with no allocation mode; Writes selects its batches.' }
if ($TraceToolPath -and (-not $AllocationBackend -or -not (Test-Path -LiteralPath $TraceToolPath -PathType Leaf))) { throw 'Tracing requires a selected allocation backend and an existing dotnet-trace executable.' }
if ($TraceToolPath) { $TraceToolPath = [IO.Path]::GetFullPath($TraceToolPath) }
$benchmarkRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$benchmarkFixturePath = Join-Path $benchmarkRoot 'eng/.fixture.env'
if (-not (Test-Path -LiteralPath $benchmarkFixturePath -PathType Leaf)) { throw 'Start the existing owned Community fixture first; this runner never starts a database.' }
$benchmarkFixture = ConvertFrom-BenchmarkFixtureEnvironment -Lines ([IO.File]::ReadAllLines($benchmarkFixturePath))
$benchmarkArtifactsRoot = Join-Path $benchmarkRoot '.artifacts/benchmarks'
[IO.Directory]::CreateDirectory($benchmarkArtifactsRoot) | Out-Null
$benchmarkLock = $null
try { $benchmarkLock = [IO.File]::Open((Join-Path $benchmarkArtifactsRoot 'operation.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw 'Another benchmark runner is active. Serialize all database tests/builds through the primary agent.' }
$benchmarkChild = $null
$benchmarkStdoutFile = $null
$benchmarkStderrFile = $null
$benchmarkOutputCopy = $null
$benchmarkErrorCopy = $null
$benchmarkFailure = $false
$benchmarkReserve = 5GB
$benchmarkSelectedId = $null
$benchmarkProvenance = $null
$benchmarkRunRoot = $null
$benchmarkTimeline = [Collections.Generic.List[object]]::new()
try {
    if (Test-FixtureDedicatedHostedRunner) { $benchmarkReserve = 2GB }
    $benchmarkSnapshot = Get-FixtureResourceSnapshot
    Assert-FixtureResources -Mode ExistingFixture -FixtureProject $benchmarkFixture.GRAPHBRIDGE_COMPOSE_PROJECT -SnapshotProvider { $benchmarkSnapshot }
    $benchmarkSelected = @($benchmarkSnapshot.RunningContainers | Where-Object {
        $_.Project -ceq $benchmarkFixture.GRAPHBRIDGE_COMPOSE_PROJECT -and $_.Service -ceq 'neo4j'
    })
    $benchmarkSelectedId = $benchmarkSelected[0].Id
    $benchmarkMetadata = Get-BenchmarkContainerMetadata -ContainerId $benchmarkSelectedId
    Assert-BenchmarkContainer -Metadata $benchmarkMetadata -Fixture $benchmarkFixture -ContainerId $benchmarkSelectedId
    $benchmarkDll = Join-Path $benchmarkRoot ("benchmarks/GraphBridge.Benchmarks/bin/$Configuration/net10.0/GraphBridge.Benchmarks.dll")
    if (-not (Test-Path -LiteralPath $benchmarkDll -PathType Leaf)) { throw 'Build the benchmark in the selected configuration separately; the runner never restores or builds.' }
    $benchmarkCommitLines = @(& git -C $benchmarkRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $benchmarkCommitLines.Count -ne 1 -or $benchmarkCommitLines[0] -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot record an exact checkout commit for benchmark provenance.' }
    $benchmarkDirtyLines = @(& git -C $benchmarkRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record checkout dirty state for benchmark provenance.' }
    $benchmarkCpu = 'not available'
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        try { $benchmarkCpu = (@(Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -ExpandProperty Name) -join '; ') }
        catch { $benchmarkCpu = 'not available' }
    }
    elseif (Test-Path -LiteralPath '/proc/cpuinfo') {
        $benchmarkCpuLine = Get-Content -LiteralPath '/proc/cpuinfo' | Where-Object { $_ -match '^model name\s*:' } | Select-Object -First 1
        if ($benchmarkCpuLine) { $benchmarkCpu = ($benchmarkCpuLine -split ':', 2)[1].Trim() }
    }
    $benchmarkRunRoot = Join-Path $benchmarkArtifactsRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($benchmarkRunRoot) | Out-Null
    $benchmarkDataDirectory = Join-Path $benchmarkRunRoot 'data'
    $benchmarkProvenance = [ordered]@{
        StartedUtc = [DateTimeOffset]::UtcNow.ToString('O'); Commit = $benchmarkCommitLines[0]; DirtyCheckout = ($benchmarkDirtyLines.Count -gt 0)
        BinarySha256 = (Get-FileHash -LiteralPath $benchmarkDll -Algorithm SHA256).Hash.ToLowerInvariant()
        Configuration = $Configuration; Cpu = $benchmarkCpu; LogicalProcessors = [Environment]::ProcessorCount
        AvailableMemoryAtStartBytes = [long]$benchmarkSnapshot.AvailableMemoryBytes; ReserveBytes = $benchmarkReserve
        MonitorHeadroomBytes = 256MB
        Container = $benchmarkMetadata; MaximumMinutes = $TimeoutMinutes; PowerShell = $PSVersionTable.PSVersion.ToString()
        SelectedMatrix = @{ Sizes = $benchmarkSizes; EdgeSizes = $benchmarkEdgeSizes; Writes = $benchmarkWrites; Samples = $Samples; Warmups = $Warmups; FetchSize = $FetchSize; Seed = $Seed }
        AllocationBackend = $AllocationBackend; Bulk = [bool]$Bulk; Tracing = [bool]$TraceToolPath
        Outcome = 'running'; InterruptedRunsMayNeedOwnedFixtureTeardown = $false
    }
    $benchmarkProvenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $benchmarkRunRoot 'runner.json') -Encoding utf8
    $benchmarkInherited = @{}
    foreach ($benchmarkEntry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) { $benchmarkInherited[$benchmarkEntry.Key] = $benchmarkEntry.Value }
    $benchmarkChildEnvironment = New-BenchmarkChildEnvironment -Inherited $benchmarkInherited -Fixture $benchmarkFixture -Provenance @{
        GRAPHBRIDGE_BENCHMARK_COMMIT = $benchmarkCommitLines[0]; GRAPHBRIDGE_BENCHMARK_IMAGE = $benchmarkPinnedImage
        GRAPHBRIDGE_BENCHMARK_CPU = $benchmarkCpu; GRAPHBRIDGE_BENCHMARK_MEMORY = [string]$benchmarkSnapshot.AvailableMemoryBytes
        GRAPHBRIDGE_BENCHMARK_DOCKER_LIMITS = ('MemoryBytes=' + $benchmarkMetadata.Memory + ';NanoCpus=' + $benchmarkMetadata.NanoCpus)
    }
    $benchmarkStartInfo = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $benchmarkStartInfo.WorkingDirectory = $benchmarkRoot
    $benchmarkStartInfo.UseShellExecute = $false
    $benchmarkStartInfo.CreateNoWindow = $true
    $benchmarkStartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $benchmarkStartInfo.RedirectStandardOutput = $true
    $benchmarkStartInfo.RedirectStandardError = $true
    if ($TraceToolPath) {
        $benchmarkStartInfo.FileName = $TraceToolPath
        foreach ($traceArgument in @('collect', '--profile', 'gc-verbose', '--providers', 'GraphBridge-AllocationProfile:0xFFFFFFFFFFFFFFFF:4', '--buffersize', '128', '--output', (Join-Path $benchmarkRunRoot 'allocations.nettrace'), '--', 'dotnet')) {
            $benchmarkStartInfo.ArgumentList.Add($traceArgument)
        }
    }
    # Execute an existing compiled DLL directly: equivalent to --no-build/--no-restore, with no MSBuild subprocess.
    foreach ($benchmarkArgument in @($benchmarkDll, ('--sizes=' + ($benchmarkSizes -join ',')), ('--edge-sizes=' + ($benchmarkEdgeSizes -join ',')), ('--writes=' + ($benchmarkWrites -join ',')),
        ('--samples=' + $Samples), ('--warmups=' + $Warmups), ('--fetch-size=' + $FetchSize), ('--seed=' + $Seed), ('--output=' + $benchmarkDataDirectory))) {
        $benchmarkStartInfo.ArgumentList.Add($benchmarkArgument)
    }
    if ($AllocationBackend) { $benchmarkStartInfo.ArgumentList.Add('--allocation-backend=' + $AllocationBackend) }
    if ($Bulk) { $benchmarkStartInfo.ArgumentList.Add('--bulk') }
    $benchmarkStartInfo.Environment.Clear()
    foreach ($benchmarkName in $benchmarkChildEnvironment.Keys) { $benchmarkStartInfo.Environment[$benchmarkName] = [string]$benchmarkChildEnvironment[$benchmarkName] }
    $benchmarkStdoutFile = [IO.File]::Create((Join-Path $benchmarkRunRoot 'stdout.log'))
    $benchmarkStderrFile = [IO.File]::Create((Join-Path $benchmarkRunRoot 'stderr.log'))
    $benchmarkChild = [Diagnostics.Process]::Start($benchmarkStartInfo)
    $benchmarkOutputCopy = $benchmarkChild.StandardOutput.BaseStream.CopyToAsync($benchmarkStdoutFile)
    $benchmarkErrorCopy = $benchmarkChild.StandardError.BaseStream.CopyToAsync($benchmarkStderrFile)
    $benchmarkProvenance.ChildPid = $benchmarkChild.Id
    $benchmarkTimeline.Add([ordered]@{ ElapsedSeconds = 0; AvailableMemoryBytes = [long]$benchmarkSnapshot.AvailableMemoryBytes })
    Wait-BenchmarkProcess -Child $benchmarkChild -ReserveBytes $benchmarkReserve -MaximumSeconds ($TimeoutMinutes * 60) -OnSample {
        param($Elapsed, $Available)
        $benchmarkTimeline.Add([ordered]@{ ElapsedSeconds = $Elapsed; AvailableMemoryBytes = [long]$Available })
    }
    if ($Bulk) {
        $bulkCompletionPath = Join-Path $benchmarkDataDirectory 'bulk-completion.json'
        if (-not (Test-Path -LiteralPath $bulkCompletionPath -PathType Leaf)) { throw 'Bulk child exited without completion evidence.' }
        $bulkCompletion = Get-Content -LiteralPath $bulkCompletionPath -Raw | ConvertFrom-Json
        if ($bulkCompletion.Completed -ne $true -or $bulkCompletion.Cleaned -ne $true -or $bulkCompletion.ShapesStable -ne $true -or $bulkCompletion.Mode -cne 'bulk-nodes' -or
            (@($bulkCompletion.BatchSizes) -join ',') -cne $Writes -or $bulkCompletion.Samples -ne $Samples -or $bulkCompletion.Warmups -ne $Warmups -or
            $bulkCompletion.SampleCount -ne ($benchmarkWrites.Length * 6 * (1 + $Samples + $Warmups)) -or
            $bulkCompletion.ProtocolCount -ne ($benchmarkWrites.Length * 6)) { throw 'Bulk completion differs from the selected experiment.' }
        $benchmarkProvenance.Outcome = 'completed-bulk-node-experiment'
    } elseif ($AllocationBackend) {
        $allocationCompletionPath = Join-Path $benchmarkDataDirectory 'allocation-completion.json'
        if (-not (Test-Path -LiteralPath $allocationCompletionPath -PathType Leaf)) { throw 'Allocation child exited without completion evidence.' }
        $allocationCompletion = Get-Content -LiteralPath $allocationCompletionPath -Raw | ConvertFrom-Json
        if ($allocationCompletion.Completed -ne $true -or $allocationCompletion.Cleaned -ne $true -or
            $allocationCompletion.Backend -cne $AllocationBackend -or $allocationCompletion.Rows -ne 100000 -or
            $allocationCompletion.Samples -ne $Samples -or $allocationCompletion.Warmups -ne $Warmups -or
            $allocationCompletion.SampleCount -ne (1 + $Samples + $Warmups)) { throw 'Allocation completion differs from selected experiment.' }
        if ($TraceToolPath -and (-not (Test-Path -LiteralPath (Join-Path $benchmarkRunRoot 'allocations.nettrace') -PathType Leaf))) { throw 'Allocation trace is missing.' }
        $benchmarkProvenance.Outcome = 'completed-allocation-experiment'
    } else {
    $benchmarkCompletion = Join-Path $benchmarkDataDirectory 'completion.json'
    $benchmarkEdgeCompletion = Join-Path $benchmarkDataDirectory 'edge-completion.json'
    if (-not (Test-Path -LiteralPath $benchmarkCompletion -PathType Leaf) -or
        -not (Test-Path -LiteralPath $benchmarkEdgeCompletion -PathType Leaf)) {
        throw 'Benchmark child exited without complete selected-matrix evidence.'
    }
    Assert-BenchmarkCompletion -Completion (Get-Content -LiteralPath $benchmarkCompletion -Raw | ConvertFrom-Json) `
        -EdgeCompletion (Get-Content -LiteralPath $benchmarkEdgeCompletion -Raw | ConvertFrom-Json) `
        -Sizes $benchmarkSizes -EdgeSizes $benchmarkEdgeSizes -Writes $benchmarkWrites
    $benchmarkProvenance.Outcome = 'completed-selected-matrix'
    }
}
catch {
    $benchmarkFailure = $true
    if ($null -ne $benchmarkProvenance) {
        $benchmarkProvenance.Outcome = 'failed-or-interrupted'
        $benchmarkProvenance.InterruptedRunsMayNeedOwnedFixtureTeardown = $true
    }
    throw
}
finally {
  $benchmarkHadPrimaryFailure = $benchmarkFailure
  $benchmarkCleanupError = $null
  try {
    # Cover failures during output redirection/initialization before the supervisor was entered too.
    if ($null -ne $benchmarkChild) {
        try {
            if (-not $benchmarkChild.HasExited) {
                $benchmarkChild.Kill($true)
                if (-not $benchmarkChild.WaitForExit(30000)) { throw 'Owned benchmark process cleanup timed out.' }
            }
            foreach ($benchmarkCopy in @($benchmarkOutputCopy, $benchmarkErrorCopy)) {
                if ($null -ne $benchmarkCopy -and -not $benchmarkCopy.Wait(10000)) { throw 'Benchmark output flush timed out.' }
            }
        }
        catch {
            $benchmarkCleanupError = $_
            $benchmarkFailure = $true
            Write-Warning 'Owned benchmark process/output cleanup could not be confirmed.'
        }
        finally {
            try { $benchmarkChild.Dispose() }
            catch { $benchmarkCleanupError = $_; $benchmarkFailure = $true }
        }
    }
    foreach ($benchmarkStream in @($benchmarkStdoutFile, $benchmarkStderrFile)) {
        if ($null -ne $benchmarkStream) {
            try { $benchmarkStream.Dispose() }
            catch { $benchmarkCleanupError = $_; $benchmarkFailure = $true }
        }
    }
    if ($benchmarkFailure -and $null -ne $benchmarkProvenance) {
        $benchmarkProvenance.Outcome = 'failed-or-interrupted'
        $benchmarkProvenance.InterruptedRunsMayNeedOwnedFixtureTeardown = $true
    }
    try {
        if ($benchmarkFailure -and $null -ne $benchmarkSelectedId) {
            $benchmarkReleaseIdle = $true
            try { $benchmarkReleaseIdle = (Get-FixtureAvailableMemoryBytes) -lt $benchmarkReserve } catch { $benchmarkReleaseIdle = $true }
            if ($benchmarkReleaseIdle) {
                $benchmarkCurrentMetadata = Get-BenchmarkContainerMetadata -ContainerId $benchmarkSelectedId
                Assert-BenchmarkContainer -Metadata $benchmarkCurrentMetadata -Fixture $benchmarkFixture -ContainerId $benchmarkSelectedId -ForCleanup
                & docker stop --time 10 $benchmarkSelectedId | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not release the verified owned idle fixture.' }
                Write-Warning 'The selected owned fixture was stopped to release shared RAM; use dedicated fixture teardown/recreation before more tests.'
            }
        }
    }
    catch { Write-Warning 'Owned idle-resource cleanup was not confirmed; no foreign resource was targeted.' }
    finally {
        if ($null -ne $benchmarkRunRoot -and $null -ne $benchmarkProvenance) {
            $benchmarkProvenance.FinishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            try {
                ConvertTo-Json -InputObject @($benchmarkTimeline.ToArray()) -Depth 4 |
                    Set-Content -LiteralPath (Join-Path $benchmarkRunRoot 'memory-samples.json') -Encoding utf8
            }
            catch { $benchmarkCleanupError = $_; $benchmarkFailure = $true }
            if ($benchmarkFailure) { $benchmarkProvenance.Outcome = 'failed-or-interrupted' }
            try { $benchmarkProvenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $benchmarkRunRoot 'runner.json') -Encoding utf8 }
            catch { $benchmarkCleanupError = $_; $benchmarkFailure = $true }
        }
    }
  }
  finally { $benchmarkLock.Dispose() }
  if (-not $benchmarkHadPrimaryFailure -and $null -ne $benchmarkCleanupError) { throw $benchmarkCleanupError }
}
Write-Output ('Benchmark evidence: ' + $benchmarkRunRoot)
