# GraphBridge Query Lab for VS Code

A local saved-evidence viewer with **Query Lab**, **Doctor** and **Verify** views. This preview is under active acceptance testing; Marketplace publication is not enabled.

Right-click a `.json` file in the Explorer and choose **Open Saved Evidence**, use the graph button in the title bar of an open `.json` file, or run **GraphBridge Query Lab: Open Saved Evidence** from the command palette and select a JSON report exported by `GraphEvidenceReport.ToJson()`, `GraphVerificationReport.ToJson()` or `GraphSemanticVerificationReport.ToJson()`. No database or .NET process is started by opening a report. HTML exports are not accepted.

For a complete offline example, build `samples/05-FluentCypher/FluentCypher.Sample.csproj`, then run its built DLL with `--lab-json` and redirect stdout to a `.json` file. This explicitly includes C# excerpts, mapped schema names and compiled Cypher. No database is contacted. Run the built DLL rather than mixing build output into the JSON file.

- Query Lab shows declared source excerpts, generated Cypher, compiler clauses/scopes, typed query patterns and captured EXPLAIN operators.
- Doctor filters retained diagnostics and schema findings, with explanations and suggested actions.
- Verify separates compilation, planning, native-versus-fixed expectations and Fluent-versus-native comparisons. Missing evidence remains **not-run**.

Statement text and compiler snapshots require explicit disclosure when generating the report. Parameter values remain excluded. Saved reports are observations supplied by their author, not signed attestations or compatibility certificates. Graphs depict query variables, not database result nodes. C# excerpts are not verified source maps; declared references are displayed as text and never opened as paths or commands.

## Optional source envelope

Raw .NET JSON reports work directly. To include declared source excerpts, wrap the report as follows:

```json
{
  "kind": "graphbridge-query-lab",
  "formatVersion": 1,
  "report": { "formatVersion": 1, "...": "a complete .NET report" },
  "sources": [{ "contractId": "movies.coactors.v1", "text": "// C# excerpt" }]
}
```

The example above illustrates the envelope; `report` must contain an actual complete report. Contract IDs must be unique and match a query in that report. Sources have no file-access authority. Unsupported format versions, inconsistent graph spans, duplicate query IDs and mismatched plan fingerprints are rejected. Unknown properties are discarded before display. Files are limited to 4 MB, nesting to 32 levels and structural values to 100,000; individual query/source/graph budgets also apply. Dense patterns use the complete text description instead of SVG.

## Build locally

Requires Node 22 or newer and VS Code 1.106 or newer. From this directory:

```sh
npm ci --ignore-scripts
npm run check
npm run build
npm run package
```

Install the resulting VSIX with **Extensions: Install from VSIX**. Dependencies are bundled; no CDN, telemetry, development server or online service is required to view evidence. The extension opens at most one panel and releases its webview state when hidden. There is no arbitrary query editor/executor, MCP adapter or standalone application in this increment.

## Explicit application-host capture

**GraphBridge Query Lab: Capture from Application Host** runs a reviewed, prebuilt application DLL inside a trusted local workspace. Select the DLL, operation and disclosure level, then confirm execution. The sample host is the built `FluentCypher.Sample.dll`; it implements the fixed `--lab-host` [protocol](HOST_PROTOCOL.md). There is no automatic build, context discovery or query editor. Credentials stay in the application's environment/configuration.

Choose offline V0 compilation, connected V1 metadata/EXPLAIN, or V2 semantic checks against an exclusively owned disposable fixture. The sample preserves its existing `GRAPHBRIDGE_NEO4J_*` configuration and V2 loopback/fixture/run acknowledgment requirements. The extension does not seed data, provision fixtures or run cleanup DDL. Capture has a 30-second deadline, output bounds and cancellation; one host runs at a time and its owned process tree is terminated on cancellation/overflow/deadline. This is a resource boundary, not a sandbox or a guarantee that remote server work stops immediately. Application code must be trusted.

Saved reports still work in restricted workspaces, with no host, database or AI invocation. Host capture is disabled there. Local acceptance includes an actual restricted VS Code 1.106.2 workspace, offline/process error paths and one disposable Community live bridge run with three query contracts. Host capture preserved graph/schema state and rejected a mismatched run. This is Windows acceptance; other editor platforms and broader deployment profiles remain unqualified. Marketplace publication is separate.

The extension is MIT licensed; bundled third-party licenses accompany the VSIX. .NET package versions and this extension version are separate. Previously qualified .NET package artifacts are not rebuilt by the extension build.
