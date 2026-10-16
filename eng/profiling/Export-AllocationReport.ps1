#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TracePath,
    [Parameter(Mandatory)] [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$reportRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $reportRepo 'eng/scripts/Benchmark.Common.ps1')
$reportTrace = (Resolve-Path -LiteralPath $TracePath).Path
$reportOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $reportOutput) { throw 'Choose a new report output directory; existing evidence is preserved.' }
$reportBuild = Join-Path $reportRepo ('.artifacts/allocation-analysis/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportBuild -Force | Out-Null
$reportSource = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'AllocationReport.cs'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="[3.2.6]" /><Compile Include="$reportSource" Link="Program.cs" /></ItemGroup>
</Project>
"@
$reportProject = Join-Path $reportBuild 'AllocationReport.csproj'
[IO.File]::WriteAllText($reportProject, $project)
function Invoke-ReportChild([string[]] $Arguments) {
    if ((Get-FixtureAvailableMemoryBytes) -lt (5GB + 512MB)) { throw 'Allocation analysis needs the 5 GiB reserve plus workload headroom.' }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $reportRepo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try { Wait-BenchmarkProcess -Child $child -ReserveBytes 5GB -MaximumSeconds 300 }
    finally { $child.Dispose() }
}
Invoke-ReportChild @('build', $reportProject, '-m:1', '-p:UseSharedCompilation=false')
Invoke-ReportChild @((Join-Path $reportBuild 'bin/Debug/net10.0/AllocationReport.dll'), $reportTrace, $reportOutput)
Copy-Item -LiteralPath (Join-Path $reportBuild 'packages.lock.json') -Destination (Join-Path $reportOutput 'analysis-packages.lock.json')
Write-Output "Allocation report and analysis dependency lock retained at $reportOutput"
