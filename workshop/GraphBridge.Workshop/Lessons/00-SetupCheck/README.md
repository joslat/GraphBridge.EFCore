# 00 · Setup check

**Goal:** confirm the pinned packages and, if configured, a reachable and safe Neo4j database.
**Track:** ★ core · **Mode:** offline + Neo4j · **Time:** ~5 min · **Builds on:** nothing

## Concepts

- The workshop runs on the in-repository GraphBridge provider, EF Core 10 and the official Neo4j .NET driver.
- Lessons marked offline need no database. Everything else needs a **disposable** Neo4j you are happy to fill and reset.
- The workshop refuses to write to a database that holds anything except workshop data.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 00
```

Offline, you see the package versions and a note that the database steps were skipped. To connect, start a disposable local Neo4j and set `GRAPHBRIDGE_WORKSHOP_URI` and `GRAPHBRIDGE_WORKSHOP_PASSWORD` as shown in [Get a database](../../README.md#get-a-database). The password is read with a masked prompt, so it never appears in a command line or your shell history.

## Walkthrough

1. **Packages.** The lesson prints the .NET runtime and the provider, EF Core and driver versions it actually loaded.
2. **Reach the database.** One read-only system call asks the server what it is:

<!-- region: Server -->
```csharp
var server = await WorkshopData.ReadAsync(lesson.Connection!,
    "CALL dbms.components() YIELD name, versions, edition RETURN name, versions[0] AS version, edition",
    record => new { Name = record["name"].As<string>(), Version = record["version"].As<string>(), Edition = record["edition"].As<string>() },
    cancellationToken);
```

3. **Safety guard.** The workshop checks the labels in the database. It continues only if the database is empty or contains workshop data, then writes one `(:GraphBridgeWorkshop)` marker node that records ownership.

## What you should see

```text
  GraphBridge provider: 0.2.0-preview.1
  ...
  ✔ connected to Neo4j
  ✔ database passes the workshop safety guard
```

## Try it yourself

1. Run the core track without a database: `dotnet run --project workshop/GraphBridge.Workshop -- run --track core --offline`. Which lessons run in full, which run only their offline part, and which are reported as not run without a database?
2. Point the workshop at `bolt://some-other-host:7687`. Read the refusal, then notice the `--allow-remote` switch it mentions. Use it only for a disposable remote database.

## Limits

- The guard is a safety net, not a permission system. Use a database dedicated to the workshop.
- `--allow-other-data` lets the workshop share a disposable database with other labels. It still refuses Person, Movie or Review nodes that existed before its ownership marker, but after that it treats every node with those labels as workshop data.

## Read more

[Troubleshooting connections](../../../../docs/reference/TROUBLESHOOTING.md) · [Supported versions](../../../../docs/reference/COMPATIBILITY.md)
