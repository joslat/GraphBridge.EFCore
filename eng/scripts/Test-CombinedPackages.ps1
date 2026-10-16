#requires -Version 7.0
[CmdletBinding()]
param([switch] $Live, [string] $CandidateFeed, [string] $CandidateVersion)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'eng/scripts/Assert-PackageMetadata.ps1')
. (Join-Path $repo 'eng/scripts/Assert-FixtureResources.ps1')
$savedEnvironment = @{}
if ($Live) {
    . (Join-Path $repo 'eng/scripts/Benchmark.Common.ps1')
    $fixture = ConvertFrom-BenchmarkFixtureEnvironment -Lines ([IO.File]::ReadAllLines((Join-Path $repo 'eng/.fixture.env')))
    Assert-FixtureResources -Mode ExistingFixture -FixtureProject $fixture.GRAPHBRIDGE_COMPOSE_PROJECT
}
$id = [Guid]::NewGuid().ToString('N')
$version = "0.1.0-packagecheck.$id"
if ([bool]$CandidateFeed -ne [bool]$CandidateVersion) { throw 'CandidateFeed and CandidateVersion must be supplied together.' }
if ($CandidateFeed) {
    $CandidateFeed = (Resolve-Path -LiteralPath $CandidateFeed).Path
    if ($CandidateVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-[a-zA-Z0-9.-]+$') { throw 'Expected explicit preview candidate version.' }
    $version = $CandidateVersion
}
$root = Join-Path $repo ".artifacts/combined-package/$id"
$feed = Join-Path $root 'packages'
$consumer = Join-Path $root 'consumer'
$cache = Join-Path $root 'cache'
New-Item -ItemType Directory -Path $feed, $consumer, $cache -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$packages = @('GraphBridge.EntityFrameworkCore', 'GraphBridge.Tooling', 'GraphBridge.Generators')
$artifacts = @()
$sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw 'Cannot identify source revision.' }
$sourceDirty = [bool](& git -C $repo status --porcelain --untracked-files=normal)

function Invoke-CheckedDotnet {
    param([string[]] $Arguments)
    # Serialized work, with half a GiB of additional start headroom above the shared reserve.
    $available = Get-FixtureAvailableMemoryBytes
    if ($available -lt 5.5GB) { throw 'Combined package verification deferred: less than 5.5 GiB available host RAM.' }
    & dotnet @Arguments
    if ($LASTEXITCODE) { throw "Combined package command failed: dotnet $($Arguments[0])." }
    if ((Get-FixtureAvailableMemoryBytes) -lt 5GB) { throw 'Memory reserve reached; defer remaining package verification.' }
}

Push-Location $repo
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($name in $packages) {
        if ($CandidateFeed) {
            Copy-Item -LiteralPath (Join-Path $CandidateFeed "$name.$version.nupkg") -Destination $feed
        } else {
            Invoke-CheckedDotnet -Arguments @('pack', "src/$name/$name.csproj", '--no-restore', '-c', 'Release', '-m:1',
                '-p:UseSharedCompilation=false', '-nodeReuse:false', "-p:Version=$version", "-p:PackageVersion=$version", '-o', $feed)
        }
        $path = Join-Path $feed "$name.$version.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $reader = [IO.StreamReader]::new($archive.GetEntry("$name.nuspec").Open())
            try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            Assert-GraphBridgePackageMetadata -Archive $archive -Manifest $manifest -RepositoryRoot $repo
            $dependencies = @($manifest.SelectNodes("//*[local-name()='dependency']") | ForEach-Object {
                [ordered]@{ id = $_.GetAttribute('id'); range = $_.GetAttribute('version') }
            })
            if ($name -eq 'GraphBridge.Tooling') {
                $provider = @($dependencies | Where-Object id -eq 'GraphBridge.EntityFrameworkCore')
                if ($provider.Count -ne 1 -or $provider[0].range -notin @($version, "[$version,)", "[$version]")) {
                    throw 'Tooling does not depend on this candidate provider version.'
                }
            }
            $artifacts += [ordered]@{ id = $name; version = $version; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); dependencies = $dependencies }
        } finally { $archive.Dispose() }
    }

    # No repository build props, central versions, targets, source references or shared package cache.
    foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        [IO.File]::WriteAllText((Join-Path $consumer $file), '<Project />', $utf8)
    }
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $consumer
    $references = ($packages | ForEach-Object { "    <PackageReference Include=`"$_`" Version=`"[$version]`" />" }) -join "`n"
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsPackable>false</IsPackable>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
$references
  </ItemGroup>
</Project>
"@
    $projectPath = Join-Path $consumer 'CombinedConsumer.csproj'
    [IO.File]::WriteAllText($projectPath, $project, $utf8)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../consumers/CombinedPackageConsumer.cs') -Destination (Join-Path $consumer 'Program.cs')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../consumers/CombinedLiveProbe.cs') -Destination $consumer
    $feedXml = [Security.SecurityElement]::Escape($feed)
    $cacheXml = [Security.SecurityElement]::Escape($cache)
    $config = @"
<configuration>
  <packageSources><clear /><add key="candidate" value="$feedXml" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="candidate"><package pattern="GraphBridge.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
  <config><add key="globalPackagesFolder" value="$cacheXml" /></config>
</configuration>
"@
    $configPath = Join-Path $consumer 'NuGet.Config'
    [IO.File]::WriteAllText($configPath, $config, $utf8)
    Invoke-CheckedDotnet -Arguments @('restore', $projectPath, '--configfile', $configPath, '--disable-parallel', '-m:1', '-nodeReuse:false')
    Invoke-CheckedDotnet -Arguments @('restore', $projectPath, '--configfile', $configPath, '--locked-mode', '--disable-parallel', '-m:1', '-nodeReuse:false')
    $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties.Name)
    $required = @($packages | ForEach-Object { "$_/$version" }) + @('Microsoft.EntityFrameworkCore/10.0.12', 'Neo4j.Driver/6.3.0')
    foreach ($name in $required) { if ($libraries -notcontains $name) { throw "Unexpected resolved package: $name" } }
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'package' }).Count) { throw 'Consumer has a non-package dependency.' }
    if ($libraries -match '^Microsoft.EntityFrameworkCore.Relational/') { throw 'Combined consumer unexpectedly has relational EF dependencies.' }
    Invoke-CheckedDotnet -Arguments @('build', $projectPath, '--no-restore', '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false')
    $runtime = Join-Path $consumer 'bin/Release/net10.0'
    if (Test-Path -LiteralPath (Join-Path $runtime 'GraphBridge.Generators.dll')) { throw 'Generator leaked into runtime output.' }
    $consumerArguments = @((Join-Path $runtime 'CombinedConsumer.dll'))
    if ($Live) {
        Assert-FixtureResources -Mode ExistingFixture -FixtureProject $fixture.GRAPHBRIDGE_COMPOSE_PROJECT
        foreach ($name in $fixture.Keys) {
            $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $fixture[$name], 'Process')
        }
        $consumerArguments += '--live'
    }
    $result = & dotnet @consumerArguments
    if ($LASTEXITCODE) { throw 'Combined package runtime acceptance failed.' }
    $result | Write-Output
    $observed = $result | ConvertFrom-Json
    if ($observed.status -ne 'pass' -or $observed.driverCalls -ne 0) { throw 'Consumer did not produce a passing offline result.' }
    if ($observed.live -ne [bool]$Live) { throw 'Consumer did not confirm the requested live execution mode.' }
    foreach ($name in @('GraphBridge.EntityFrameworkCore', 'GraphBridge.Tooling')) {
        $assembly = @($observed.assemblies | Where-Object name -eq $name)
        if ($assembly.Count -ne 1 -or $assembly[0].productVersion.Split('+')[0] -ne $version) { throw "Loaded $name is not the packed candidate." }
    }
    [IO.File]::WriteAllText((Join-Path $root 'verification.json'), ([ordered]@{
        formatVersion = 1; sourceCommit = $sourceCommit; sourceDirty = $sourceDirty; packages = $artifacts;
        consumer = $observed; live = $(if ($Live) { 'pass' } else { 'not-run' }); published = $false
    } | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "Combined PackageReference-only consumer verified; evidence: $root. Live passed: $([bool]$Live). Nothing published."
} finally {
    . (Join-Path $repo 'eng/scripts/Restore-ProcessEnvironment.ps1')
    Restore-ProcessEnvironment -Snapshot $savedEnvironment
    Pop-Location
}
