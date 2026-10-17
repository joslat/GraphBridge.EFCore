#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string[]] $Arguments, [string] $Log,
    [ValidateRange(1, 3600)][int] $MaximumSeconds = 600)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-FixtureResources.ps1')
. (Join-Path $PSScriptRoot 'Benchmark.Common.ps1')
if ((Get-FixtureAvailableMemoryBytes) -lt 6GB) { throw 'Work deferred: reserve 5 GiB plus 1 GiB workload headroom.' }
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
# Run where the caller is: PowerShell's location, not the directory the PowerShell process started in.
$start.WorkingDirectory = (Get-Location -PSProvider FileSystem).ProviderPath
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['MSBUILDDISABLENODEREUSE'] = '1'
foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($start)
$stdout = $child.StandardOutput.ReadToEndAsync()
$stderr = $child.StandardError.ReadToEndAsync()
try {
    Wait-BenchmarkProcess -Child $child -ReserveBytes 5GB -MaximumSeconds $MaximumSeconds
} finally {
    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    if ($Log) {
        $logPath = [IO.Path]::GetFullPath($Log)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($logPath)) | Out-Null
        [IO.File]::WriteAllText($logPath, $output)
    }
    Write-Output $output
    $child.Dispose()
}
