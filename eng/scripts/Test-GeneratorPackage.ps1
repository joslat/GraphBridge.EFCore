[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot '../../eng/scripts/Assert-PackageMetadata.ps1')
$checkId = [Guid]::NewGuid().ToString('N')
$checkRoot = Join-Path $repoRoot ".artifacts/generator-package/$checkId"
$packageRoot = Join-Path $checkRoot 'packages'
$consumerRoot = Join-Path $checkRoot 'consumer'
$packageVersion = "0.1.0-packagecheck.$checkId"
New-Item -ItemType Directory -Path $packageRoot, $consumerRoot -Force | Out-Null

# A unique local version ensures an older global-cache package cannot pass this check.
& dotnet pack (Join-Path $repoRoot 'src/GraphBridge.Generators/GraphBridge.Generators.csproj') --no-restore -m:1 -p:UseSharedCompilation=false "-p:PackageVersion=$packageVersion" -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Generator packaging failed.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packagePath = Join-Path $packageRoot "GraphBridge.Generators.$packageVersion.nupkg"
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    if ($names -notcontains 'analyzers/dotnet/cs/GraphBridge.Generators.dll') { throw 'Analyzer payload is missing.' }
    if (@($names | Where-Object { $_ -match '^(lib|ref)/' }).Count -ne 0) { throw 'Generator package contains runtime/reference assets.' }
    if ($names -notcontains 'README.md') { throw 'Package README is missing.' }
    $reader = [System.IO.StreamReader]::new($archive.GetEntry('GraphBridge.Generators.nuspec').Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    Assert-GraphBridgePackageMetadata -Archive $archive -Manifest $manifest -RepositoryRoot $repoRoot
    if ($manifest.SelectNodes("//*[local-name()='dependency']").Count -ne 0) { throw 'Analyzer package introduces package dependencies.' }
} finally { $archive.Dispose() }

$providerPath = [System.Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj'))
$feedPath = [System.Security.SecurityElement]::Escape($packageRoot)
$projectPath = Join-Path $consumerRoot 'PackageConsumer.csproj'
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestoreAdditionalProjectSources>$feedPath</RestoreAdditionalProjectSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="GraphBridge.Generators" Version="$packageVersion" PrivateAssets="all" />
    <ProjectReference Include="$providerPath" />
  </ItemGroup>
</Project>
"@
[System.IO.File]::WriteAllText($projectPath, $project)
Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/GraphBridge.Generators.Tests/Consumer/ConsumerModel.cs') -Destination $consumerRoot
& dotnet restore $projectPath --disable-parallel
if ($LASTEXITCODE -ne 0) { throw 'Packaged analyzer consumer restore failed.' }
& dotnet build $projectPath --no-restore -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Packaged analyzer consumer compilation failed.' }
if (Test-Path -LiteralPath (Join-Path $consumerRoot 'bin/Debug/net10.0/GraphBridge.Generators.dll')) {
    throw 'Analyzer was copied to the runtime output.'
}
Write-Host 'Packaged analyzer layout and real PackageReference consumer verified; no database used.'
