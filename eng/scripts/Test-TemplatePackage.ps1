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
    # The GraphRAG starter writes the default GraphRAG schema. Sample 07's tests create the same names under their own
    # migration, so the starter needs a fixture they haven't used: refuse up front rather than fail halfway.
    $templateCompose = @('compose', '--project-name', $templateFixture.GRAPHBRIDGE_COMPOSE_PROJECT, '-f', (Join-Path $templateRepo 'eng/docker-compose.yml'),
        '--env-file', (Join-Path $templateRepo 'eng/.fixture.env'))
    $templateSchema = "SHOW CONSTRAINTS YIELD name WHERE name = 'graphrag_document_key' RETURN count(*) AS n;" |
        & docker @templateCompose exec -T neo4j sh -c 'exec cypher-shell -u neo4j -p "${NEO4J_AUTH#*/}" --format plain' 2>$null
    if ($LASTEXITCODE) { throw 'Could not read the fixture schema for the GraphRAG starter.' }
    if (($templateSchema | Select-Object -Last 1).Trim() -ne '0') {
        throw 'The default GraphRAG schema already exists in this fixture, so the GraphRAG starter would not set up; run -Live on a fresh fixture.'
    }
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
        foreach ($name in @('GraphBridge.EntityFrameworkCore', 'GraphBridge.GraphRag', 'GraphBridge.Templates')) {
            Copy-Item -LiteralPath (Join-Path $CandidateFeed "$name.$templateVersion.nupkg") -Destination $templateFeed
        }
    } else {
        Invoke-TemplateDotnet pack src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj --no-restore '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false' "-p:PackageVersion=$templateVersion" -o $templateFeed
        if ($LASTEXITCODE) { throw 'Template provider package failed.' }
        Invoke-TemplateDotnet pack src/GraphBridge.GraphRag/GraphBridge.GraphRag.csproj --no-restore '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false' "-p:PackageVersion=$templateVersion" -o $templateFeed
        if ($LASTEXITCODE) { throw 'Template GraphRAG package failed.' }
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
        $reader = [IO.StreamReader]::new($archive.GetEntry('content/graphbridge/App/GraphBridgeStarter.csproj').Open())
        try { [xml] $starter = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    # A template is checked against the exact EF Core and driver versions it pins, so a published template keeps passing
    # after the repository moves on. A template packed from this checkout must pin the repository's central versions.
    $templatePins = @($starter.Project.ItemGroup.PackageReference | Where-Object Include -cne 'GraphBridge.EntityFrameworkCore' |
        ForEach-Object { '{0}/{1}' -f $_.Include, $_.Version.Trim('[', ']') })
    if (($templatePins | ForEach-Object { $_.Split('/')[0] } | Sort-Object) -join ',' -cne 'Microsoft.EntityFrameworkCore,Neo4j.Driver') { throw 'Template application must pin exactly EF Core and Neo4j.Driver.' }
    if (-not $CandidateFeed) {
        [xml] $central = Get-Content -LiteralPath (Join-Path $templateRepo 'Directory.Packages.props') -Raw
        foreach ($pin in $templatePins) {
            $id, $version = $pin.Split('/')
            $tested = @($central.Project.ItemGroup.PackageVersion | Where-Object Include -ceq $id)[0].Version
            # A central range such as [10.0.13, 11.0.0) (PL-46) is tested at its minimum.
            if ($tested -match '^\[\s*([^,\]\s]+)\s*,') { $tested = $Matches[1] }
            if ($tested -cne $version) { throw "Template pins $pin, but the repository tests $id $tested." }
        }
    }
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
    foreach ($required in @("GraphBridge.EntityFrameworkCore/$templateVersion") + $templatePins) {
        if ($assets.libraries.PSObject.Properties.Name -cnotcontains $required) { throw 'Generated application did not resolve a tested exact package version.' }
    }
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count) { throw 'Generated application references checkout source.' }
    Invoke-TemplateDotnet build $solution --no-restore '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false'
    if ($LASTEXITCODE) { throw 'Generated solution build failed.' }
    Invoke-TemplateDotnet test (Join-Path $templateConsumer "Tests/$templateName.Tests.csproj") --no-build --no-restore
    if ($LASTEXITCODE) { throw 'Generated contract tests failed.' }
    # The GraphRAG starter (GR-64): the same isolated install, then create, two restores, exact versions, build and offline tests.
    $ragName = 'VerifiedRag'
    $ragConsumer = Join-Path ([IO.Path]::GetTempPath()) ('GraphBridgeRagTemplate-' + $templateId)
    Invoke-TemplateDotnet new graphbridge-graphrag --name $ragName --provider-version $templateVersion --output $ragConsumer --debug:custom-hive $templateHive
    foreach ($path in @('App/Program.cs', 'App/StandIns.cs', 'App/docs/graph-retrieval.md', 'LICENSE', 'global.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ragConsumer $path))) { throw "Generated GraphRAG starter is missing $path." }
    }
    [xml] $ragProject = Get-Content -LiteralPath (Join-Path $ragConsumer "App/$ragName.csproj") -Raw
    $ragPins = @($ragProject.Project.ItemGroup.PackageReference | Where-Object { $_.Include -notlike 'GraphBridge.*' } |
        ForEach-Object { '{0}/{1}' -f $_.Include, $_.Version.Trim('[', ']') })
    if (($ragPins | ForEach-Object { $_.Split('/')[0] } | Sort-Object) -join ',' -cne 'Microsoft.EntityFrameworkCore,Microsoft.Extensions.AI.Abstractions,Neo4j.Driver') {
        throw 'GraphRAG starter must pin exactly EF Core, Microsoft.Extensions.AI.Abstractions and Neo4j.Driver.'
    }
    if (-not $CandidateFeed) {
        [xml] $central = Get-Content -LiteralPath (Join-Path $templateRepo 'Directory.Packages.props') -Raw
        foreach ($pin in $ragPins) {
            $id, $version = $pin.Split('/')
            $tested = @($central.Project.ItemGroup.PackageVersion | Where-Object Include -ceq $id)[0].Version
            if ($tested -match '^\[\s*([^,\]\s]+)\s*,') { $tested = $Matches[1] }
            if ($tested -cne $version) { throw "GraphRAG starter pins $pin, but the repository tests $id $tested." }
        }
    }
    $ragSolution = Join-Path $ragConsumer "$ragName.slnx"
    $ragConfig = Join-Path $ragConsumer 'NuGet.Config'
    Invoke-TemplateDotnet nuget add source $templateFeed --name GraphBridgePreview --configfile $ragConfig
    Invoke-TemplateDotnet restore $ragSolution --configfile $ragConfig --disable-parallel '-m:1' '-nodeReuse:false'
    Invoke-TemplateDotnet restore $ragSolution --configfile $ragConfig --disable-parallel '-m:1' '-nodeReuse:false' --locked-mode
    $ragAssets = Get-Content -LiteralPath (Join-Path $ragConsumer 'App/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($required in @("GraphBridge.GraphRag/$templateVersion", "GraphBridge.EntityFrameworkCore/$templateVersion") + $ragPins) {
        if ($ragAssets.libraries.PSObject.Properties.Name -cnotcontains $required) { throw "Generated GraphRAG starter did not resolve $required." }
    }
    if (@($ragAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count -ne 0) { throw 'Generated GraphRAG starter references checkout source.' }
    Invoke-TemplateDotnet build $ragSolution --no-restore '-m:1' '-p:UseSharedCompilation=false' '-nodeReuse:false'
    Invoke-TemplateDotnet test (Join-Path $ragConsumer "Tests/$ragName.Tests.csproj") --no-build --no-restore
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
        # It writes the default GraphRAG schema (checked absent above), so run any suite that uses it before this, not after.
        $ragApp = Join-Path $ragConsumer "App/$ragName.csproj"
        foreach ($command in @(@('setup'), @('ingest'))) {
            Invoke-TemplateDotnet run --project $ragApp --no-build --no-restore -- @command | Write-Output
        }
        $ragAnswer = @(Invoke-TemplateDotnet run --project $ragApp --no-build --no-restore -- ask "Which route finds passages that contain the question's words?")
        $ragAnswer | Write-Output
        if (-not ($ragAnswer | Where-Object { $_ -match '^\s+\[bytes \d+-\d+\] "' })) { throw 'Generated GraphRAG starter did not answer with a checked citation.' }
        Invoke-TemplateDotnet run --project $ragApp --no-build --no-restore -- cleanup | Write-Output
    }
    [ordered]@{ version = $templateVersion; consumer = $templateConsumer; graphRagConsumer = $ragConsumer; package = $templatePackage; liveCrudPassed = [bool]$Live; graphRagLivePassed = [bool]$Live; published = $false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $templateRoot 'verification.json') -Encoding utf8
    Write-Output "Template package, isolated install, fresh builds, two provider tests and three GraphRAG starter tests verified. Live CRUD and GraphRAG starter run: $([bool]$Live). Evidence: $templateRoot"
} finally {
    # An unset variable must stay unset: an empty one overrides Docker's --env-file and breaks the fixture's teardown.
    . (Join-Path $PSScriptRoot 'Restore-ProcessEnvironment.ps1')
    Restore-ProcessEnvironment -Snapshot $templateSavedEnvironment
    Pop-Location
}
