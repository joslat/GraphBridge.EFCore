[CmdletBinding()]
param([ValidateRange(4, 20)][int] $Samples = 6)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'eng/scripts/Benchmark.Common.ps1')
$run = Join-Path $repo ('.artifacts/generator-build/' + [Guid]::NewGuid().ToString('N'))
$consumer = Join-Path $run 'consumer'
$frozen = Join-Path $run 'frozen'
$emitted = Join-Path $run 'emitted'
[IO.Directory]::CreateDirectory($consumer) | Out-Null
[IO.Directory]::CreateDirectory($frozen) | Out-Null
$observations = [Collections.Generic.List[object]]::new()
$memory = [Collections.Generic.List[object]]::new()
$project = Join-Path $consumer 'BuildConsumer.csproj'

function Invoke-MeasuredBuildCommand {
    param([string[]] $Arguments, [string] $Label, [switch] $ExpectedFailure)
    $available = Get-FixtureAvailableMemoryBytes
    if ($available -lt 7GB) { throw 'Build preflight requires 7 GiB free (5 GiB reserve plus headroom).' }
    $memory.Add([pscustomobject]@{ Label = $Label; AvailableBytes = $available })
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $repo
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $child.Start()) { throw 'Could not start owned build.' }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        # Expected compiler failures still use the same ownership/memory/timeout monitor.
        try {
            Wait-BenchmarkProcess -Child $child -ReserveBytes 5GB -HeadroomBytes 512MB -MaximumSeconds 180 -OnSample {
                param($elapsed, $availableNow)
                $memory.Add([pscustomobject]@{ Label = $Label; AvailableBytes = $availableNow })
            }
        } catch {
            if (-not $ExpectedFailure -or -not $child.HasExited -or $child.ExitCode -eq 0 -or
                $_.Exception.Message -notlike 'Benchmark child failed with exit code *') { throw }
        }
        $watch.Stop()
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $run ($Label + '.stdout.log')), $out)
        [IO.File]::WriteAllText((Join-Path $run ($Label + '.stderr.log')), $err)
        if ($ExpectedFailure -and ($child.ExitCode -eq 0 -or $out -notmatch 'error GBSG008')) {
            throw 'Expected actual compiler GBSG008 failure was not observed.'
        }
        if (-not $ExpectedFailure -and $out -notmatch 'Build succeeded\.|Restored|up-to-date for restore') {
            throw 'Expected build/restore success output was not observed.'
        }
        return [pscustomobject]@{ Milliseconds = $watch.Elapsed.TotalMilliseconds; ExitCode = $child.ExitCode }
    } finally {
        if ($child.Id -and -not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
}

$provider = [Security.SecurityElement]::Escape((Join-Path $repo 'src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj'))
$generator = [Security.SecurityElement]::Escape((Join-Path $repo 'src/GraphBridge.Generators/GraphBridge.Generators.csproj'))
[IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <ActiveGenerator Condition="'`$(ActiveGenerator)' == ''">true</ActiveGenerator>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$provider" />
    <ProjectReference Include="$generator" OutputItemType="Analyzer" ReferenceOutputAssembly="false" Condition="'`$(ActiveGenerator)' == 'true'" />
    <Compile Include="../frozen/**/*.cs" Condition="'`$(ActiveGenerator)' == 'false'" />
  </ItemGroup>
</Project>
"@)
Copy-Item -LiteralPath (Join-Path $repo 'tests/GraphBridge.Generators.Tests/Consumer/ConsumerModel.cs') -Destination $consumer
Invoke-MeasuredBuildCommand -Arguments @('restore', $project, '--disable-parallel') -Label 'restore' | Out-Null
$common = @('build', $project, '--no-restore', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')
Invoke-MeasuredBuildCommand -Arguments ($common + @('-p:EmitCompilerGeneratedFiles=true', "-p:CompilerGeneratedFilesOutputPath=$emitted")) -Label 'capture' | Out-Null
$generated = @(Get-ChildItem -LiteralPath $emitted -Recurse -Filter '*.cs')
if ($generated.Count -ne 2) { throw 'Expected attributes and one generated registration.' }
foreach ($file in $generated) { Copy-Item -LiteralPath $file.FullName -Destination $frozen }
$modelPath = Join-Path $consumer 'ConsumerModel.cs'
$model = [IO.File]::ReadAllText($modelPath)
[IO.File]::WriteAllText($modelPath, $model.Replace(', SchemaPolicy = GraphSchemaPolicy.ExistingConstraints', ''))
try { Invoke-MeasuredBuildCommand -Arguments $common -Label 'missing-policy' -ExpectedFailure | Out-Null }
finally { [IO.File]::WriteAllText($modelPath, $model) }
Invoke-MeasuredBuildCommand -Arguments $common -Label 'policy-repaired' | Out-Null

# Force compilation in both arms. Same authored and generated C#; only optional generator/analyzer execution differs.
# Include two warmup pairs. Alternate order to expose, not eliminate, shared-host/order noise.
for ($iteration = -2; $iteration -lt $Samples; $iteration++) {
    $order = if ($iteration % 2 -eq 0) { @('true', 'false') } else { @('false', 'true') }
    foreach ($active in $order) {
        $label = "iteration-$iteration-active-$active"
        $result = Invoke-MeasuredBuildCommand -Arguments ($common + @('-t:Rebuild', '-p:BuildProjectReferences=false', "-p:ActiveGenerator=$active")) -Label $label
        $observations.Add([pscustomobject]@{ Iteration = $iteration; ActiveGenerator = $active -eq 'true'; Milliseconds = $result.Milliseconds; ExitCode = $result.ExitCode })
        Write-Host "$label completed"
    }
}
if (Test-Path -LiteralPath (Join-Path $consumer 'bin/Debug/net10.0/GraphBridge.Generators.dll')) { throw 'Generator leaked into runtime output.' }
$hashes = @($generated | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
$report = [ordered]@{
    Completed = $true; Scope = 'Three-entity consumer, serial forced Debug builds, same generated C# frozen in the control; no database or IDE timing.'
    SamplesPerArm = $Samples; WarmupPairs = 2; SchemaDiagnosticBuildFailedAndRecovered = $true
    SourceCommit = (& git -C $repo rev-parse HEAD); DirtyCheckout = [bool](& git -C $repo status --porcelain)
    Sdk = (& dotnet --version); GeneratorSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'src/GraphBridge.Generators/bin/Debug/netstandard2.0/GraphBridge.Generators.dll')).Hash
    GeneratedFiles = $hashes; Observations = $observations; MemorySamples = $memory
}
[IO.File]::WriteAllText((Join-Path $run 'report.json'), ($report | ConvertTo-Json -Depth 12))
Write-Host "Completed generator build experiment: $run"
