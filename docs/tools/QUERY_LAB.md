# GraphBridge Query Lab

Query Lab shows each registered query's C# source, its generated Cypher, its typed graph pattern and clause scopes, and any captured plans and result comparisons, side by side. Use it to review what a query really means before you trust it. It comes in two forms that present the same captured [Doctor](DOCTOR.md) and [Verify](VERIFY.md) evidence: a **self-contained HTML report** rendered by `GraphQueryLab.RenderHtml`, and a **VS Code extension** that opens the JSON evidence and can capture fresh evidence from your application.

```csharp
using System.Text.Json;
using GraphBridge.Tooling.Diagnostics;

// Capture offline, explicitly disclosing statements and typed snapshots.
var verification = GraphVerify.Offline(db, catalog, includeStatements: true);

// Form 1: a static HTML report. Sources are optional C# excerpts keyed by contract ID.
var html = GraphQueryLab.RenderHtml(verification, sources);
await File.WriteAllTextAsync("query-lab.html", html, cancellationToken);

// Form 2: the same evidence as a JSON envelope for the VS Code extension.
var envelope = JsonSerializer.Serialize(new
{
    kind = "graphbridge-query-lab",
    formatVersion = 1,
    report = JsonSerializer.Deserialize<JsonElement>(verification.ToJson()),
    sources = sources.Select(s => new { contractId = s.ContractId, text = s.Text })
}, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync("query-lab.json", envelope, cancellationToken);
```

Here `sources` is a list of `GraphQuerySource(contractId, text)`. Neither form compiles queries, connects to Neo4j or runs your code when you open a report.

## Static HTML report

`GraphQueryLab.RenderHtml(report, sources = null)` accepts a `GraphEvidenceReport` (Doctor), a `GraphVerificationReport` (Verify V0/V1) or a `GraphSemanticVerificationReport` (Verify V2) and returns one HTML string. It uses that report directly; it never merges unrelated plans or results by matching query text.

Each `GraphQuerySource` must name a query present in the report, at most once, with up to 32,768 characters of text. Query Lab escapes, fingerprints and displays the excerpt as data. It does not verify that the excerpt produced the query: that is your claim. The sample and workshop extract excerpts from the very source files they compile.

Statement and snapshot text must be disclosed at capture time with `includeStatements: true`. A default, redacted report renders an omission notice instead of a graph; Query Lab never recovers or guesses redacted text.

## Runnable sample

Build [sample 05](../../samples/05-FluentCypher/README.md) first, then run its DLL in one mode and redirect standard output to a UTF-8 file. Running the built DLL keeps build logs out of the report; use PowerShell 7 for UTF-8 redirection.

```powershell
dotnet build samples/05-FluentCypher/FluentCypher.Sample.csproj
$app = 'samples/05-FluentCypher/bin/Debug/net10.0/FluentCypher.Sample.dll'

dotnet $app --lab > query-lab.html                 # offline V0: no connection or configuration
dotnet $app --lab-inspect > query-lab-plans.html   # adds metadata inspection and EXPLAIN (V1)
dotnet $app --lab-semantic > query-lab-results.html  # adds V2 results on the owned disposable fixture
dotnet $app --lab-json > query-lab.json            # offline JSON envelope for VS Code
```

`--lab-inspect` uses the four `GRAPHBRIDGE_NEO4J_*` variables, like Verify V1. `--lab-semantic` also needs the acknowledged disposable fixture, markers and run ID described in the [V2 fixture contract](VERIFY.md#graphbridge-verify-v2-semantic-comparison); it uses actor Ada and year threshold 2000 so the report shows both duplicate credits and unmatched optional rows. No mode provisions, repairs or cleans a fixture. Lab modes deliberately include the sample's mapped Cypher and C# excerpts, but never parameter or result values. Exit codes: `0` when the selected tier passes, `1` for non-passing evidence, `2` for invalid configuration. A V0 pass leaves plans and semantics not run.

## Reading the report

| Panel | What it shows |
|---|---|
| Source & contract | The optional C# excerpt, its source reference and fingerprint, declared ordering, duplicate policy and budgets. |
| Generated Cypher | The captured parameterized statement with line numbers, copyable text, statement fingerprint and broad parameter kinds. |
| Graph & clause scope | Query variables as nodes, directed relationships, pattern occurrences, which clause owns each WHERE, WITH scope and counts, returned columns, and links to the matching Cypher lines. |
| Evidence & limits | The EXPLAIN operator tree when captured, the separate native-versus-fixed and Fluent-versus-native comparisons, sanitized differences, and explicit not-run or blocked states. |

The coactor pattern distinguishes incoming traversal from the relationship's physical source and target. Shared-movie aggregation shows `count(DISTINCT movie)` next to the physical row count. The OPTIONAL panel shows where its predicate belongs: moving it after WITH can remove null-preserved rows. Query Lab never rewrites a query.

The sample's two optional queries make this concrete. `OptionalMovies` attaches the predicate to OPTIONAL MATCH and returns eight rows; `FilteredOptionalMovies` moves it after WITH and returns six, because the two people without a qualifying movie disappear. Checking the second factory against the first one's null-preserving contract correctly reports a row-count failure, while the native reference still passes its eight-row expectation. That is a deliberate semantic difference, not a translation defect.

A circle in the diagram is a query variable, not a database node. Repeated paths keep their bounds and direction without inventing individual edges. Nested EXISTS has no diagram yet; its text remains available. Cypher line links are not C# source mappings. Plan trees show only redacted operators and their parents, not arguments, timings or performance conclusions.

## Portable presentation and bounds

The HTML embeds its CSS and SVG, has no JavaScript, makes no network requests and needs no server; a content security policy blocks scripts. Disclosure controls, clause links and text selection work without scripts. Save the file to share it, and use the browser's print command for PDF or paper; printing keeps the accessible node and relationship tables that accompany every diagram. The layout supports narrow screens and reduced-motion preferences.

Rendering fails explicitly above 32,768 characters per source excerpt, 65,536 characters per statement or 4,000,000 characters of HTML. Diagrams are limited to 32 nodes and 64 steps per pattern; larger snapshots keep their exact tables with a diagram-limit notice. Compiler snapshot bounds are [separate](../features/FLUENT_CYPHER.md#supported-clauses-and-values). These limits do not bound query execution or server work.

## VS Code extension

The **GraphBridge Query Lab** extension in [`extensions/graphbridge-query-lab`](../../extensions/graphbridge-query-lab/README.md) shows saved evidence in three views: **Query Lab** (source, Cypher, clauses and scopes, typed patterns, plans), **Doctor** (filterable diagnostics and schema findings with actions) and **Verify** (compilation, planning and both comparison stages per contract). Missing evidence stays `not-run`.

### Install

The extension is not on the Marketplace; build the VSIX locally. You need Node 22 or newer and VS Code 1.106 or newer.

```powershell
cd extensions/graphbridge-query-lab
npm ci --ignore-scripts
npm run check
npm run build
npm run package     # writes graphbridge-query-lab-0.1.0.vsix
```

Install the file with **Extensions: Install from VSIX** in the command palette. Dependencies are bundled: no CDN, telemetry, development server or online service is used to view evidence.

### Open saved evidence

Right-click a `.json` file in the Explorer and choose **Open Saved Evidence**, use the graph button in the title bar of an open `.json` file, or run **GraphBridge Query Lab: Open Saved Evidence** and pick the file. The extension accepts:

- a raw report from `GraphEvidenceReport.ToJson()`, `GraphVerificationReport.ToJson()` or `GraphSemanticVerificationReport.ToJson()`, or
- the source envelope shown at the top of this page: `kind: "graphbridge-query-lab"`, `formatVersion: 1`, a complete `report` and optional `sources` (`contractId`, `text`) whose IDs are unique and match queries in that report.

HTML exports are not accepted. Opening a file starts no database connection and no .NET process, and works in restricted (untrusted) workspaces. Files are limited to 4 MB, 32 levels of nesting and 100,000 structural values. Unsupported format versions, inconsistent graph spans, duplicate query IDs and mismatched plan fingerprints are rejected; unknown properties are dropped before display. Dense patterns fall back to their text description.

### Capture from an application host

**GraphBridge Query Lab: Capture from Application Host** runs a reviewed, prebuilt .NET application DLL from your workspace and displays the evidence it returns. The extension starts `dotnet <your.dll> --lab-host`, writes one JSON request to standard input and reads one JSON response from standard output.

1. Open a **trusted** workspace. The command is disabled in restricted workspaces.
2. Select the DLL. Its resolved path, including symlinks, must lie inside the workspace.
3. Choose the operation: **Compile offline (V0)**, **Inspect and EXPLAIN (V1)** or **Verify disposable fixture (V2)**.
4. Choose disclosure: **Redacted evidence**, or **Include statements and source** (mapped names, generated Cypher and C# excerpts).
5. Confirm. V1 and V2 ask you to confirm database access; V2 asks you to confirm the fixture is exclusively owned and disposable.

The request carries only the operation and your choices; it never contains query text, parameters, credentials, paths or assembly names:

```json
{
  "formatVersion": 1,
  "requestId": "0123456789abcdef0123456789abcdef",
  "operation": "offline",
  "includeStatements": false,
  "acknowledgeDatabase": false,
  "acknowledgeDisposable": false
}
```

The host replies with `{ "formatVersion": 1, "requestId": …, "result": <source envelope> }` or, instead of `result`, one fixed `error` code: `invalid-request`, `configuration`, `capture-failed` or `output-limit`. A delivered response exits `0` even when the report inside it fails; the viewer shows the report's own status. Requests are at most 4,096 bytes. The extension limits standard output to 4,000,000 bytes, standard error to 16,384 bytes (never displayed) and the run to 30 seconds, and runs one host at a time without a shell. Cancellation, a deadline or an overflow terminates the host's process tree and discards partial output.

Your application implements the host: it owns its context, catalog, configuration and fixture rules. [Sample 05](../../samples/05-FluentCypher/LabHost.cs) is a complete implementation; the full contract is in the [host protocol](../../extensions/graphbridge-query-lab/HOST_PROTOCOL.md). The host inherits the environment VS Code was started with, so set connection variables there; the extension has no credential UI. It does not build code, discover contexts, seed data, provision fixtures or run cleanup.

### Trust and disclosure

- A host is executable application code, not a sandbox. Run only hosts you have reviewed, and never arbitrary third-party executables. Hosts must not leave background processes running after the response.
- Process termination is a resource bound, not proof that server work stopped.
- Saved reports are observations supplied by whoever created them, not signed attestations or compatibility certificates.
- Source excerpts are displayed as text; declared source references are never opened as paths or commands. No path or command from a report or the webview can start a capture.
- Parameter values, result values, ownership tokens, credentials and exception text are never embedded. Statements and excerpts appear only when disclosed at capture; host-supplied source can contain literals, so review it before sharing.

## Limits

- Query Lab presents evidence; it has no query editor and executes nothing on its own.
- The extension is a local preview: not on the Marketplace, verified on Windows only, and without an MCP adapter or standalone application.
- The `--lab-host` protocol is application-owned. Another application must implement it with its own reviewed catalog and configuration.

## Try it

- Workshop lesson [28 · Query Lab](../../workshop/GraphBridge.Workshop/Lessons/28-QueryLab/README.md) writes `query-lab.html` and `query-lab.json` for the workshop's contracts.
- [Sample 05](../../samples/05-FluentCypher/README.md) provides the `--lab` modes above and the `--lab-host` entry point for the extension.

Design decisions: [ADR-0043](../architecture/adr/ADR-0043.md) (static report) and [ADR-0046](../architecture/adr/ADR-0046.md) (VS Code and application-owned capture). Component map: [packages and components](../reference/PACKAGES_AND_COMPONENTS.md). Evidence: [verification summary](../../tests/verification/VERIFICATION_SUMMARY.md).
