# GraphBridgeStarter

This preview starter uses actual GraphBridge NuGet packages and public EF APIs. Its console program is the same guarded CRUD/rollback consumer verified by the GraphBridge repository. It creates schema explicitly, verifies ownership before writes, and deletes only its random test ID. This prototype intentionally runs against the repository's disposable fixture; adapting it to another database requires reviewing setup and ownership policy.

Requirements: .NET SDK 10.0.401, GraphBridge version `0.0.0-provider-version-required` available in your local/private feed, and access to NuGet for pinned upstream packages. GraphBridge is not yet published to nuget.org.

```powershell
dotnet nuget add source /path/to/local-feed --name GraphBridgePreview --configfile NuGet.Config
dotnet restore GraphBridgeStarter.slnx --configfile NuGet.Config --disable-parallel
dotnet build GraphBridgeStarter.slnx --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test Tests/GraphBridgeStarter.Tests.csproj --no-build
dotnet run --project App/GraphBridgeStarter.csproj --no-build
```

Tests are offline and reject all driver access. Running the application requires the owned fixture environment (`GRAPHBRIDGE_INTEGRATION`, URI/database, username/password and generated fixture ID); without it the application refuses before connecting. Never add credentials to source or configuration files. The repository's `Test-TemplatePackage.ps1 -Live` verifies generation, package resolution, build, offline tests and actual CRUD with the existing guarded fixture. Do not run competing fixture workloads; retain the repository's memory reserve.

GraphBridge uses async query/save operations and supports a documented LINQ subset. Inspect generated Cypher with `ToQueryString()`; unsupported translations throw. This template adds no automatic restore, Docker launch, migrations or publication post-action. Original starter code is MIT licensed; package dependencies retain their own terms.
