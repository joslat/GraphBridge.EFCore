#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$quickRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'Benchmark.Common.ps1')
$quickEnv = Join-Path $quickRepo 'eng/.fixture.env'
$quickRoot = Join-Path $quickRepo ('.artifacts/blogging-quickstart/' + [Guid]::NewGuid().ToString('N'))
$quickReserve = if (Test-FixtureDedicatedHostedRunner) { 2GB } else { 5GB }
$quickShell = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$quickWatch = [Diagnostics.Stopwatch]::StartNew()
$quickFailure = $null
$quickStarted = $false
$quickProject = $null
$quickLock = $null
$quickStages = [Collections.Generic.List[object]]::new()

function Invoke-QuickstartChild {
    param([string] $Name, [string] $Executable, [string[]] $Arguments)
    if ((Get-FixtureAvailableMemoryBytes) -lt ($quickReserve + 512MB)) {
        throw 'Quickstart requires the host memory reserve plus 512 MiB workload headroom.'
    }
    $stageWatch = [Diagnostics.Stopwatch]::StartNew()
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $quickRepo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = $null
    $copyOut = $null
    $copyErr = $null
    $stdout = [IO.File]::Create((Join-Path $quickRoot "$Name.stdout.log"))
    $stderr = [IO.File]::Create((Join-Path $quickRoot "$Name.stderr.log"))
    try {
        Write-Output "Blogging quickstart: $Name"
        $child = [Diagnostics.Process]::Start($start)
        $copyOut = $child.StandardOutput.BaseStream.CopyToAsync($stdout)
        $copyErr = $child.StandardError.BaseStream.CopyToAsync($stderr)
        # Reuse the existing tested owned-process timeout/memory supervisor.
        Wait-BenchmarkProcess -Child $child -ReserveBytes $quickReserve -MaximumSeconds 600
        $copyOut.GetAwaiter().GetResult() | Out-Null
        $copyErr.GetAwaiter().GetResult() | Out-Null
        $quickStages.Add([ordered]@{ Stage = $Name; Seconds = $stageWatch.Elapsed.TotalSeconds; ExitCode = $child.ExitCode })
    }
    finally {
        if ($null -ne $child) {
            if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit(30000) | Out-Null }
            if ($null -ne $copyOut) { $copyOut.GetAwaiter().GetResult() | Out-Null }
            if ($null -ne $copyErr) { $copyErr.GetAwaiter().GetResult() | Out-Null }
            $child.Dispose()
        }
        $stdout.Dispose()
        $stderr.Dispose()
    }
}

try {
    [IO.Directory]::CreateDirectory((Join-Path $quickRepo '.artifacts')) | Out-Null
    $quickLock = [IO.File]::Open((Join-Path $quickRepo '.artifacts/blogging-quickstart.lock'),
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if (Test-Path -LiteralPath $quickEnv) {
        throw 'An owned fixture environment already exists. Reuse it with Run-BloggingSample.ps1; the one-command quickstart requires a fresh fixture and will not remove an existing one.'
    }
    Assert-FixtureResources -Mode Startup
    [IO.Directory]::CreateDirectory($quickRoot) | Out-Null
    $quickSample = Join-Path $quickRepo 'samples/01-Blogging-CodeFirst/Blogging.App/Blogging.App.csproj'
    Invoke-QuickstartChild restore dotnet @('restore', $quickSample, '--locked-mode', '--disable-parallel')
    Invoke-QuickstartChild build dotnet @('build', $quickSample, '--no-restore', '-m:1', '-p:UseSharedCompilation=false')
    # Recheck full startup headroom after builds before launching the bounded server.
    Assert-FixtureResources -Mode Startup
    $quickStarted = $true
    Invoke-QuickstartChild startup $quickShell @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Start-Neo4jFixture.ps1'))
    $quickProject = Get-FixtureComposeProjectFromEnvironmentFile -Path $quickEnv
    Invoke-QuickstartChild sample $quickShell @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Run-BloggingSample.ps1'), '-PrepareSchema', '-NoBuild')
    Get-Content -LiteralPath (Join-Path $quickRoot 'sample.stdout.log')
}
catch { $quickFailure = $_; throw }
finally {
    try {
        if ($quickStarted -and (Test-Path -LiteralPath $quickEnv)) {
            $cleanupProject = Get-FixtureComposeProjectFromEnvironmentFile -Path $quickEnv
            if ($null -ne $quickProject -and $cleanupProject -cne $quickProject) {
                throw 'Fixture identity changed during the quickstart; refusing teardown.'
            }
            & (Join-Path $PSScriptRoot 'Stop-Neo4jFixture.ps1')
        }
    }
    catch {
        if ($null -eq $quickFailure) { $quickFailure = $_; throw }
        Write-Warning 'Quickstart fixture cleanup failed; inspect the owned fixture using its retained environment. The original failure is preserved.'
    }
    finally {
        if (Test-Path -LiteralPath $quickRoot) {
            [ordered]@{ Completed = ($null -eq $quickFailure); StartedUtc = [DateTimeOffset]::UtcNow.Subtract($quickWatch.Elapsed).ToString('O');
                ElapsedSeconds = $quickWatch.Elapsed.TotalSeconds; Stages = @($quickStages.ToArray());
                FixtureEnvironmentRemoved = (-not (Test-Path -LiteralPath $quickEnv)) } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $quickRoot 'result.json') -Encoding utf8
            Write-Output "Quickstart evidence: $quickRoot"
        }
        if ($null -ne $quickLock) { $quickLock.Dispose() }
    }
}
