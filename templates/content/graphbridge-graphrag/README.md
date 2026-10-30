# GraphRagStarter

A starter for GraphBridge's experimental GraphRAG toolkit: ingest Markdown into Neo4j, then ask questions and get answers whose every quote is checked against the stored bytes, or a refusal. It uses actual GraphBridge NuGet packages and public APIs. The toolkit is experimental: its API may change in any preview, and the project acknowledges diagnostic `GBRAG001` in `Directory.Build.props`.

It runs offline by default: a hashing embedder and an extractive chat client in `App/StandIns.cs` stand in for models, so you need no key. To use a real model, register your `IChatClient` and `IEmbeddingGenerator` in `Starter.AddStarter`, from any Microsoft.Extensions.AI provider, and set `Starter.EmbeddingSet` to the model's name and dimensions.

Requirements: .NET SDK 10.0.401, GraphBridge version `0.0.0-provider-version-required` in your feed, NuGet for the pinned upstream packages, and a Neo4j 2026.09 or later database you can write to.

```powershell
dotnet restore GraphRagStarter.slnx --disable-parallel
dotnet build GraphRagStarter.slnx --no-restore
dotnet test Tests/GraphRagStarter.Tests.csproj --no-build
# Point the four GRAPHBRIDGE_NEO4J_* variables (URI, database, username, password) at your database, then:
dotnet run --project App -- setup
dotnet run --project App -- ingest
dotnet run --project App -- ask "Which route finds passages that contain the question's words?"
dotnet run --project App -- cleanup
```

- `setup` previews the schema plan (key constraints, a vector index and a full-text index), then applies it as your own migration. The toolkit never creates schema by itself. The plan has no predecessor, so it is the database's first migration: on a database with other migrations, `setup` applies nothing and says why. To add it to one, give the plan the last migration's ID as its predecessor.
- `ingest` reads `App/docs`, or a folder you name. Run it again and unchanged files are skipped.
- `ask` prints the answer and each citation's byte span and quote, or why it refused.
- `cleanup` removes what was ingested. The schema stays: dropping indexes is a reviewed migration of its own.

The tests are offline: the schema plan, the stand-in embedder, and the citation check. Never put credentials in source or configuration files. The GraphRAG documentation is at https://github.com/joslat/GraphBridge.EFCore/tree/main/docs/graphrag. Original starter code is MIT licensed; package dependencies keep their own terms.
