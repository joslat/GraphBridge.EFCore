# Design-time command sample

This fourth sample demonstrates tooling around the three [model-authoring workflows](../README.md). It adds no new runtime authoring authority. See the [command contract and limitations](../../docs/tools/DESIGN_TIME.md).

Run from the repository root with the pinned SDK and restored dependencies:

```powershell
dotnet tool install dotnet-ef --version 10.0.12 --tool-path .artifacts/tools/ef
dotnet restore samples/04-DesignTime/DesignTime.App --locked-mode
dotnet build samples/04-DesignTime/DesignTime.App --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
# Stop if restore/build fails. Windows local-tool executable shown; omit .exe on Unix.
.artifacts/tools/ef/dotnet-ef.exe dbcontext list --project samples/04-DesignTime/DesignTime.App --no-build --json
.artifacts/tools/ef/dotnet-ef.exe dbcontext info --project samples/04-DesignTime/DesignTime.App --context DesignContext --no-build --json
dotnet run --project samples/04-DesignTime/DesignTime.App --no-build -- list
dotnet run --project samples/04-DesignTime/DesignTime.App --no-build -- preview initial
```

If the exact tool is already installed at that path, verify its `--version` instead of reinstalling. The factory intentionally ignores live connection environment settings and rejects forwarded design-time arguments. These metadata commands need no running database. The application-declared `initial` migration proposes one uniqueness constraint on `GraphBridgeDesignPerson.Id`; preview never creates it.

For **read-only live status**, set `GRAPHBRIDGE_URI`, `GRAPHBRIDGE_DATABASE`, `GRAPHBRIDGE_USERNAME` and `GRAPHBRIDGE_PASSWORD` in the process environment using your chosen secret/configuration mechanism, then run:

```powershell
dotnet run --project samples/04-DesignTime/DesignTime.App --no-build -- status initial
$statusExit = $LASTEXITCODE
```

An untouched database reports missing migration initialization and exits 3. This is a successful observation of an unready migration store, not a connection failure. The host never initializes, applies, repairs or drops anything. Use only an authorized database; repository acceptance uses the [owned fixture](../../eng/README.md), serial execution and the shared-memory reserve.

The source CLI `GraphBridge.Tool` continues to provide ontology/scaffolding commands. This sample is an application-owned migration catalog host, not a new `dotnet ef` plugin command or a generic assembly loader. `list` reports declared plans, not database history. Future richer commands remain separate proposals.
