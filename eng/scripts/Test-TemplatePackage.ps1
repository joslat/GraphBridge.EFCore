#requires -Version 7.0
[CmdletBinding()]
param([switch] $Live, [string] $CandidateFeed, [string] $CandidateVersion)

$ErrorActionPreference = 'Stop'
$templateRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot '../../eng/scripts/Assert-PackageMetadata.ps1')
. (Join-Path $templateRepo 'eng/scripts/Assert-FixtureResources.ps1')
$templateSavedEnvironment = @{}
function Invoke-TemplateDotnet {
    if ((Get-FixtureAvailableMemoryBytes) -lt 5.5GB) { throw 'Template verification deferred: less than 5.5 GiB available host RAM.' }
    & dotnet @args
    if ($LASTEXITCODE) { throw "Template verification failed: dotnet $($args[0])." }
    if ((Get-FixtureAvailableMemoryBytes) -lt 5GB) { throw 'Memory reserve reached; defer remaining template verification.' }
}
if ($Live) {
    . (Join-Path $PSScriptRoot '../../eng/scripts/Benchmark.Common.ps1')
    $templateFixture = ConvertFrom-BenchmarkFixtureEnvironment -Lines ([IO.File]::ReadAllLines((Join-Path $templateRepo 'eng/.fixture.env')))
    Assert-FixtureResources -Mode ExistingFixture -FixtureProject $templateFixture.GRAPHBRIDGE_COMPOSE_PROJECT
}
$templateId = [Guid]::NewGuid().ToString('N')
$templateVersion = '0.1.0-templatecheck.' + $templateId
if ([bool]$CandidateFeed -ne [bool]$CandidateVersion) { throw 'CandidateFeed and CandidateVersion must be supplied together.' }
if ($CandidateFeed) {
    $CandidateFeed = (Resolve-Path -LiteralPath $CandidateFeed).Path
    if ($CandidateVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-[a-zA-Z0-9.-]+$') { throw 'Expected explicit preview candidate version.' }
    $templateVersion = $CandidateVersion
}
$templateRoot = Join-Path $templateRepo ('.artifacts/template-package/' + $templateId)
$templateFeed = Join-Path $templateRoot 'feed'
$templateHive = Join-Path $templateRoot 'hive'
# Outside the checkout: no ancestor repository build props or project reference can rescue this consumer.
$templateConsumer = Join-Path ([IO.Path]::GetTempPath()) ('GraphBridgeTemplate-' + $templateId)
$templateName = 'VerifiedGraph'
New-Item -ItemType Directory -Path $templateFeed, $templateConsumer -Force | Out-Null
Push-Location $templateRepo
try {
    if ($CandidateFeed) {
        foreach ($name in @('GraphBridge.EntityFrameworkCore', 'GraphBridge.Templates')) {
            Copy-Item -LiteralPath (Join-Path $CandidateFeed "$name.$templateVersion.nupkg") -Destination $templateFeed
        }
    } else {
        Invoke-TemplateDotnet pack src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj --no-restore '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false' "-p:PackageVersion=$templateVersion" -o $templateFeed
        if ($LASTEXITCODE) { throw 'Template provider package failed.' }
        Invoke-TemplateDotnet pack templates/GraphBridge.Templates.csproj '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false' "-p:PackageVersion=$templateVersion" -o $templateFeed
        if ($LASTEXITCODE) { throw 'Template package failed.' }
    }
    $templatePackage = Join-Path $templateFeed "GraphBridge.Templates.$templateVersion.nupkg"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($templatePackage)
    try {
        $reader = [IO.StreamReader]::new($archive.GetEntry('GraphBridge.Templates.nuspec').Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Assert-GraphBridgePackageMetadata -Archive $archive -Manifest $manifest -RepositoryRoot $templateRepo
        if (@($archive.Entries | Where-Object { $_.FullName -like '*.dll' -or $_.FullName -match '/(bin|obj)/' }).Count -ne 0) { throw 'Template contains generated binaries or build artifacts.' }
        if ($manifest.SelectNodes("//*[local-name()='dependency']").Count -ne 0) { throw 'Template must not declare runtime dependencies.' }
        $reader = [IO.StreamReader]::new($archive.GetEntry('content/graphbridge/App/Program.cs').Open())
        try { $program = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($program -cne [IO.File]::ReadAllText((Join-Path $templateRepo 'eng/consumers/ProviderLiveConsumer.cs'))) { throw 'Template demo differs from maintained package consumer.' }
    } finally { $archive.Dispose() }
    Invoke-TemplateDotnet new install $templatePackage --debug:custom-hive $templateHive
    if ($LASTEXITCODE) { throw 'Isolated template installation failed.' }
    Invoke-TemplateDotnet new graphbridge --name $templateName --provider-version $templateVersion --output $templateConsumer --debug:custom-hive $templateHive
    if ($LASTEXITCODE) { throw 'Template instantiation failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $templateConsumer 'App/Program.cs')) -or
        -not (Test-Path -LiteralPath (Join-Path $templateConsumer 'LICENSE')) -or
        -not (Test-Path -LiteralPath (Join-Path $templateConsumer 'global.json'))) { throw 'Generated template is missing linked source, license or SDK pin.' }
    $solution = Join-Path $templateConsumer "$templateName.slnx"
    $config = Join-Path $templateConsumer 'NuGet.Config'
    Invoke-TemplateDotnet nuget add source $templateFeed --name GraphBridgePreview --configfile $config
    if ($LASTEXITCODE) { throw 'Local template feed configuration failed.' }
    Invoke-TemplateDotnet restore $solution --configfile $config --disable-parallel '-m:1' '-nodeReuse:false'
    if ($LASTEXITCODE) { throw 'Fresh template restore failed.' }
    Invoke-TemplateDotnet restore $solution --configfile $config --disable-parallel '-m:1' '-nodeReuse:false' --locked-mode
    if ($LASTEXITCODE) { throw 'Fresh template locked restore failed.' }
    $assets = Get-Content -LiteralPath (Join-Path $templateConsumer 'App/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($required in @("GraphBridge.EntityFrameworkCore/$templateVersion", 'Microsoft.EntityFrameworkCore/10.0.12', 'Neo4j.Driver/6.3.0')) {
        if ($assets.libraries.PSObject.Properties.Name -cnotcontains $required) { throw 'Generated application did not resolve a tested exact package version.' }
    }
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count) { throw 'Generated application references checkout source.' }
    Invoke-TemplateDotnet build $solution --no-restore '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false'
    if ($LASTEXITCODE) { throw 'Generated solution build failed.' }
    Invoke-TemplateDotnet test (Join-Path $templateConsumer "Tests/$templateName.Tests.csproj") --no-build --no-restore
    if ($LASTEXITCODE) { throw 'Generated contract tests failed.' }
    if ($Live) {
        foreach ($name in $templateFixture.Keys) {
            $templateSavedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $templateFixture[$name], 'Process')
        }
        Assert-FixtureResources -Mode ExistingFixture -FixtureProject $templateFixture.GRAPHBRIDGE_COMPOSE_PROJECT
        $liveOutput = @(Invoke-TemplateDotnet run --project (Join-Path $templateConsumer "App/$templateName.csproj") --no-build --no-restore)
        if ($LASTEXITCODE) { throw 'Generated application live run failed.' }
        $liveOutput | Write-Output
        if ($liveOutput -cnotcontains 'Package-only live CRUD verified: create, parameterized fresh read, update/version, explicit rollback, delete and absence.') { throw 'Generated application did not prove live CRUD completion.' }
    }
    [ordered]@{ version = $templateVersion; consumer = $templateConsumer; package = $templatePackage; liveCrudPassed = [bool]$Live; published = $false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $templateRoot 'verification.json') -Encoding utf8
    Write-Output "Template package, isolated install, fresh build and two tests verified. Live CRUD: $([bool]$Live). Evidence: $templateRoot"
} finally {
    foreach ($name in $templateSavedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $templateSavedEnvironment[$name], 'Process') }
    Pop-Location
}
