#requires -Version 7.0
[CmdletBinding()]
param([switch] $NoBuild, [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'Assert-FixtureResources.ps1')
Push-Location $repo
try {
    foreach ($project in @('tests/GraphBridge.UnitTests', 'tests/GraphBridge.ToolingTests',
        'tests/GraphBridge.Generators.Tests', 'benchmarks/GraphBridge.Benchmarks.Tests')) {
        if ((Get-FixtureAvailableMemoryBytes) -lt 6GB) { throw 'Offline suite deferred: reserve 5 GiB plus 1 GiB workload headroom.' }
        $arguments = @('test', $project, '--no-restore', '-c', $Configuration, '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false')
        if ($NoBuild) { $arguments += '--no-build' }
        & (Join-Path $PSScriptRoot 'Invoke-GuardedDotNet.ps1') -Arguments $arguments
        if ((Get-FixtureAvailableMemoryBytes) -lt 5GB) { throw 'Shared reserve reached; remaining suites deferred.' }
    }
} finally { Pop-Location }
