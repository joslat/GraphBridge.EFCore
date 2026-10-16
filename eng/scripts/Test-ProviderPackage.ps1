[CmdletBinding()]
param([switch] $Live)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot '../../eng/scripts/Assert-PackageMetadata.ps1')
$packagePreviousEnvironment = @{}
if ($Live) {
    . (Join-Path $PSScriptRoot '../../eng/scripts/Benchmark.Common.ps1')
    $packageFixturePath = Join-Path $repoRoot 'eng/.fixture.env'
    if (-not (Test-Path -LiteralPath $packageFixturePath -PathType Leaf)) { throw 'Live package acceptance requires the owned disposable fixture.' }
    $packageFixture = ConvertFrom-BenchmarkFixtureEnvironment -Lines ([IO.File]::ReadAllLines($packageFixturePath))
    Assert-FixtureResources -Mode ExistingFixture -FixtureProject $packageFixture.GRAPHBRIDGE_COMPOSE_PROJECT
}
$packageWatch = [Diagnostics.Stopwatch]::StartNew()
$checkId = [Guid]::NewGuid().ToString('N')
$checkRoot = Join-Path $repoRoot ".artifacts/provider-package/$checkId"
$packageRoot = Join-Path $checkRoot 'packages'
$consumerRoot = Join-Path $checkRoot 'consumer'
$packageVersion = "0.1.0-packagecheck.$checkId"
$utf8 = [System.Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $packageRoot, $consumerRoot -Force | Out-Null

# Run from the repository so global.json selects the exact SDK, even if the caller starts elsewhere.
Push-Location $repoRoot
try {
    # Each invocation gets a unique local version; cached packages cannot stand in for the artifact just built.
    & dotnet pack (Join-Path $repoRoot 'src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj') --no-restore -m:1 -p:UseSharedCompilation=false "-p:PackageVersion=$packageVersion" -o $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Provider packaging failed.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packagePath = Join-Path $packageRoot "GraphBridge.EntityFrameworkCore.$packageVersion.nupkg"
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    $dependencyVersions = [ordered]@{}
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        $runtimeDll = 'lib/net10.0/GraphBridge.EntityFrameworkCore.dll'
        if ($names -notcontains $runtimeDll) { throw 'Provider net10.0 runtime assembly is missing.' }
        if ($names -notcontains 'README.md') { throw 'Provider package README is missing.' }
        $dlls = @($names | Where-Object { $_ -match '\.dll$' })
        if ($dlls.Count -ne 1 -or $dlls[0] -ne $runtimeDll) { throw 'Provider package contains unexpected assemblies.' }
        if (@($names | Where-Object { $_ -match '^(analyzers|tools|build|buildTransitive)/' }).Count -ne 0) {
            throw 'Provider package unexpectedly contains analyzer, tool or build assets.'
        }
        $nuspec = $archive.GetEntry('GraphBridge.EntityFrameworkCore.nuspec')
        if ($null -eq $nuspec) { throw 'Provider package manifest is missing.' }
        $reader = [System.IO.StreamReader]::new($nuspec.Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Assert-GraphBridgePackageMetadata -Archive $archive -Manifest $manifest -RepositoryRoot $repoRoot
        $packageId = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']").InnerText
        $actualVersion = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']").InnerText
        if ($packageId -ne 'GraphBridge.EntityFrameworkCore' -or $actualVersion -ne $packageVersion) {
            throw 'Provider package identity does not match this invocation.'
        }
        $dependencies = @($manifest.SelectNodes("//*[local-name()='dependency']"))
        $expected = @{ 'Microsoft.EntityFrameworkCore' = '10.0.12'; 'Neo4j.Driver' = '6.3.0' }
        if ($dependencies.Count -ne $expected.Count) { throw 'Provider must have exactly EF Core and Neo4j.Driver direct dependencies.' }
        foreach ($dependency in $dependencies) {
            $id = $dependency.GetAttribute('id')
            $version = $dependency.GetAttribute('version')
            if (-not $expected.ContainsKey($id) -or $dependencyVersions.Contains($id)) {
                throw 'Provider package has an unexpected or duplicated dependency.'
            }
            $pin = $expected[$id]
            # SDK pack commonly emits a minimum version for a plain PackageReference. Record that honestly;
            # the clean consumer below must still resolve the exact repository-tested patch versions.
            if ($version -notin @($pin, "[$pin,)", "[$pin]")) {
                throw "Provider dependency '$id' does not match the tested package pin."
            }
            $dependencyVersions[$id] = $version
        }
    } finally { $archive.Dispose() }

    $projectPath = Join-Path $consumerRoot 'PackageConsumer.csproj'
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="GraphBridge.EntityFrameworkCore" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@
    # This local props file prevents repository-only build configuration from supplying missing consumer settings.
    [System.IO.File]::WriteAllText((Join-Path $consumerRoot 'Directory.Build.props'), '<Project />', $utf8)
    [System.IO.File]::WriteAllText($projectPath, $project, $utf8)
    $feedPath = [System.Security.SecurityElement]::Escape($packageRoot)
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="provider-package-check" value="$feedPath" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    $nugetConfigPath = Join-Path $consumerRoot 'NuGet.Config'
    [System.IO.File]::WriteAllText($nugetConfigPath, $nugetConfig, $utf8)

    $consumer = @'
using System.Reflection;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

var driver = DispatchProxy.Create<IDriver, RejectTransport>();
var probe = (RejectTransport)(object)driver;
using (var context = new PackageContext(driver))
{
    Require(context.Database.ProviderName == "GraphBridge.EntityFrameworkCore", "Unexpected provider registration.");
    var databaseType = context.GetService<IDatabase>().GetType();
    Require(databaseType.FullName == "GraphBridge.EntityFrameworkCore.Update.Neo4jDatabase"
        && databaseType.BaseType == typeof(Database), "Provider did not resolve the non-relational EF Database implementation.");
    Require(context.GetService<ITypeMappingSource>().GetType().BaseType == typeof(TypeMappingSource),
        "Provider did not resolve the non-relational type-mapping service.");
    Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore.Relational"),
        "Relational EF services were unexpectedly loaded.");

    var manifest = context.Database.GetGraphSchemaManifest();
    Require(manifest.Nodes.Count == 1 && manifest.Relationships.Count == 0, "Unexpected finalized graph storage shape.");
    var node = manifest.Nodes.Single();
    Require(node.Label == "PackageItem" && node.KeyProperties.SequenceEqual(new[] { "id" }), "Application key mapping was not retained.");
    Require(manifest.Fingerprint.Length == 64 && manifest.CanonicalJson.Contains("PackageItem", StringComparison.Ordinal),
        "Finalized storage witness is missing.");

    var value = "package-consumer-value-not-in-cypher";
    var cypher = context.Items.Where(item => item.Name == value && item.Score > 7391)
        .OrderBy(item => item.Name).Take(3).ToQueryString();
    Require(cypher.Contains("MATCH (n:`PackageItem`)", StringComparison.Ordinal)
        && cypher.Contains("$p0", StringComparison.Ordinal) && cypher.Contains("$p1", StringComparison.Ordinal)
        && cypher.Contains("LIMIT", StringComparison.Ordinal), "Expected parameterized Cypher was not produced.");
    Require(!cypher.Contains(value, StringComparison.Ordinal) && !cypher.Contains("7391", StringComparison.Ordinal),
        "Query values leaked into generated Cypher.");

    var itemAlias = Graph.Node<PackageItem>();
    var fluent = context.Cypher().Match(itemAlias)
        .Where(itemAlias.Property(item => item.Name).Eq(value))
        .Return(() => new { itemAlias.Value.Name, itemAlias.Value.Score })
        .OrderBy(itemAlias.Property(item => item.Name)).Limit(3).ToCommand();
    Require(fluent.Text.Contains("MATCH (n0:`PackageItem`)", StringComparison.Ordinal)
        && fluent.Text.Contains("RETURN n0.`name` AS c0, n0.`score` AS c1", StringComparison.Ordinal)
        && fluent.Text.Contains("ORDER BY c0 ASC", StringComparison.Ordinal), "Packaged fluent clause compilation failed.");
    Require(!fluent.Text.Contains(value, StringComparison.Ordinal) && Equals(fluent.Parameters["p0"], value),
        "Packaged fluent command did not retain parameter isolation.");
    var lambda = context.Cypher().Match(itemAlias)
        .Where(() => itemAlias.Value.Name == value)
        .Return(() => new { itemAlias.Value.Name, itemAlias.Value.Score })
        .OrderBy(() => itemAlias.Value.Name).Limit(3).ToCommand();
    Require(lambda.Text == fluent.Text && Equals(lambda.Parameters["p0"], value),
        "Packaged lambda predicate and ordering did not compile to the explicit form.");

    var rejected = false;
    try { _ = context.Items.Where(item => UnsupportedPredicate.Evaluate(item.Name)).ToQueryString(); }
    catch (Neo4jQueryTranslationException) { rejected = true; }
    Require(rejected && UnsupportedPredicate.Calls == 0, "Unsupported predicate was not rejected before client execution.");
    Require(probe.Calls == 0, "Offline consumer attempted transport.");
    Console.WriteLine("Provider PackageReference consumer verified: non-relational EF services, finalized model, manifest, parameterized Cypher and fail-closed translation.");
    Console.WriteLine("Storage fingerprint: " + manifest.Fingerprint);
}
Require(probe.Calls == 0, "Context disposal unexpectedly touched the borrowed driver.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public sealed class PackageItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public sealed class PackageContext(IDriver driver) : DbContext(new DbContextOptionsBuilder<PackageContext>().UseNeo4j(driver, "offline_package_check").Options)
{
    public DbSet<PackageItem> Items => Set<PackageItem>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<PackageItem>().ToGraphNode("PackageItem");
        item.HasKey(value => value.Id);
        item.Property(value => value.Id).HasGraphPropertyName("id").ValueGeneratedNever();
        item.Property(value => value.Name).HasGraphPropertyName("name").IsRequired();
        item.Property(value => value.Score).HasGraphPropertyName("score");
    }
}

public static class UnsupportedPredicate
{
    public static int Calls { get; private set; }
    public static bool Evaluate(string value)
    {
        Calls++;
        throw new InvalidOperationException("Client predicate must never execute.");
    }
}

public class RejectTransport : DispatchProxy
{
    public int Calls { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("Offline package consumer attempted a driver operation.");
    }
}
'@
    [System.IO.File]::WriteAllText((Join-Path $consumerRoot 'Program.cs'), $consumer, $utf8)
    & dotnet restore $projectPath --configfile $nugetConfigPath --disable-parallel
    if ($LASTEXITCODE -ne 0) { throw 'Packaged provider consumer restore failed.' }
    & dotnet restore $projectPath --configfile $nugetConfigPath --locked-mode --disable-parallel
    if ($LASTEXITCODE -ne 0) { throw 'Packaged provider consumer locked restore failed.' }

    $assets = Get-Content -LiteralPath (Join-Path $consumerRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties.Name)
    foreach ($required in @("GraphBridge.EntityFrameworkCore/$packageVersion", 'Microsoft.EntityFrameworkCore/10.0.12', 'Neo4j.Driver/6.3.0')) {
        if ($libraries -notcontains $required) { throw "Clean consumer did not resolve tested package '$required'." }
    }
    if (@($libraries | Where-Object { $_ -match '^(GraphBridge\.(Tooling|Tool|Generators)|Microsoft\.CodeAnalysis[^/]*|Microsoft\.EntityFrameworkCore\.Relational|dotNetRdf[^/]*|YamlDotNet)/' }).Count -ne 0) {
        throw 'Clean runtime consumer unexpectedly depends on relational EF, tooling, analyzers or ontology libraries.'
    }
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count -ne 0) {
        throw 'Clean consumer resolved a project reference instead of package-only dependencies.'
    }

    & dotnet build $projectPath --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Packaged provider consumer compilation failed.' }
    & dotnet run --project $projectPath --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Packaged provider offline consumer verification failed.' }

    if ($Live) {
        # Same isolated package-only project; no application project/test-harness reference is added.
        [IO.File]::WriteAllText((Join-Path $consumerRoot 'Program.cs'),
            [IO.File]::ReadAllText((Join-Path $repoRoot 'eng/consumers/ProviderLiveConsumer.cs')), $utf8)
        & dotnet build $projectPath --no-restore --no-incremental -m:1 -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'Packaged provider live consumer compilation failed.' }
        foreach ($name in $packageFixture.Keys) {
            $packagePreviousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $packageFixture[$name], 'Process')
        }
        Assert-FixtureResources -Mode ExistingFixture -FixtureProject $packageFixture.GRAPHBRIDGE_COMPOSE_PROJECT
        $packageLiveOutput = @(& dotnet run --project $projectPath --no-build --no-restore)
        if ($LASTEXITCODE -ne 0) { throw 'Packaged provider live CRUD consumer failed.' }
        $packageLiveOutput | Write-Output
        if ($packageLiveOutput -cnotcontains 'Package-only live CRUD verified: create, parameterized fresh read, update/version, explicit rollback, delete and absence.') {
            throw 'Live package consumer exited without its verified CRUD completion marker; no live acceptance claimed.'
        }
    }

    $result = [ordered]@{
        package = 'GraphBridge.EntityFrameworkCore'
        version = $packageVersion
        runtimeAsset = 'lib/net10.0/GraphBridge.EntityFrameworkCore.dll'
        nuspecDependencies = $dependencyVersions
        resolvedEfCore = '10.0.12'
        resolvedDriver = '6.3.0'
        databaseAccess = [bool]$Live
        liveCrudPassed = [bool]$Live
        elapsedSeconds = $packageWatch.Elapsed.TotalSeconds
        published = $false
    }
    [System.IO.File]::WriteAllText((Join-Path $checkRoot 'verification.json'), ($result | ConvertTo-Json -Depth 5), $utf8)
    Write-Host "Packaged provider layout, dependency graph and offline consumer verified. Live CRUD requested and passed: $([bool]$Live). No package published."
    Write-Host "Reviewable package and evidence: $checkRoot"
} finally {
    . (Join-Path $PSScriptRoot '../../eng/scripts/Restore-ProcessEnvironment.ps1')
    Restore-ProcessEnvironment -Snapshot $packagePreviousEnvironment
    Pop-Location
}
