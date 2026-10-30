# GraphBridgeStarter

This preview starter uses GraphBridge's NuGet packages and public EF APIs. Its console program creates the schema it needs explicitly, saves, queries, updates with a row version, reads with Fluent Cypher, rolls back a transaction and deletes only its random test ID. Because it writes, it accepts only a disposable local database it can identify, and refuses any other.

Requirements: .NET SDK 10.0.401 and access to nuget.org, which serves GraphBridge `0.0.0-provider-version-required` and the pinned EF Core and Neo4j.Driver packages.

```powershell
dotnet restore GraphBridgeStarter.slnx --configfile NuGet.Config --disable-parallel
dotnet build GraphBridgeStarter.slnx --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test Tests/GraphBridgeStarter.Tests.csproj --no-build
dotnet run --project App/GraphBridgeStarter.csproj --no-build
```

Tests are offline and reject all driver access. The application refuses before connecting unless it finds a disposable local database: `GRAPHBRIDGE_INTEGRATION=1`, `GRAPHBRIDGE_NEO4J_URI=bolt://127.0.0.1:17687`, `GRAPHBRIDGE_NEO4J_DATABASE=neo4j`, `GRAPHBRIDGE_NEO4J_USERNAME`, `GRAPHBRIDGE_NEO4J_PASSWORD` and a `GRAPHBRIDGE_FIXTURE_ID` marker. To use your own database, replace those guards in `App/Program.cs` with your own setup. Never add credentials to source or configuration files.

GraphBridge uses async query/save operations and supports a documented LINQ subset. Inspect generated Cypher with `ToQueryString()`; unsupported translations throw. This template adds no automatic restore, Docker launch, migrations or publication post-action. Original starter code is MIT licensed; package dependencies retain their own terms.
