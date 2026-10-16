# Query Lab verification

Requires Node 24+, npm, the pinned .NET SDK, and at least 6 GiB free RAM (5 GiB shared reserve plus workload headroom). From the repository root, serialize these commands:

```powershell
npm ci --prefix extensions/graphbridge-query-lab
npm run check --prefix extensions/graphbridge-query-lab
npm run build --prefix extensions/graphbridge-query-lab
npm ci --prefix tests/GraphBridge.QueryLab.Tests
node tests/GraphBridge.QueryLab.Tests/prepare.mjs
node tests/GraphBridge.QueryLab.Tests/run.mjs
node tests/GraphBridge.QueryLab.Tests/run.mjs host
node tests/GraphBridge.QueryLab.Tests/adapter.mjs
```

Preparation builds the actual sample and fake process host, and creates fresh offline compiler evidence. Connected reports are reviewed [synthetic contract fixtures](fixtures/README.md), not hidden maintainer artifacts. Nothing in this sequence opens a database. Host tests cover real .NET execution, malformed output, budgets, cancellation and owned descendants. The editor adapter test mocks VS Code APIs while executing the real offline host.

## Browser and actual editor

`node tests/GraphBridge.QueryLab.Tests/browser.mjs` uses installed Chrome, with optional `GRAPHBRIDGE_CHROME_EXECUTABLE`. It checks keyboard interaction, narrow/forced-color layout, captured semantic rows, escaping and CSP. No browser is downloaded automatically.

For actual packaged-editor acceptance, build a VSIX with `npm run package --prefix extensions/graphbridge-query-lab`, then install it into an isolated directory using your installed VS Code CLI:

```powershell
code --user-data-dir .artifacts/vscode/profile --extensions-dir .artifacts/vscode/extensions --install-extension extensions/graphbridge-query-lab/graphbridge-query-lab-0.1.0.vsix --force
node tests/GraphBridge.QueryLab.Tests/editor.mjs
$env:GRAPHBRIDGE_TEST_UNTRUSTED = '1'
node tests/GraphBridge.QueryLab.Tests/editor.mjs
Remove-Item Env:GRAPHBRIDGE_TEST_UNTRUSTED
```

Set `GRAPHBRIDGE_VSCODE_EXECUTABLE` to the actual Electron/Code executable when it is not found in the conventional install location. Set `GRAPHBRIDGE_TEST_EXTENSION_PATH` when the isolated installed extension path differs. Editor startup requires 6.5 GiB free and uses loopback CDP port 58183. Close only owned test instances. Windows is the qualified platform; configurable paths do not imply macOS/Linux acceptance.

Saved-report editor interaction and a separate live bridge test are established layers. A single fully automated native-dialog → live database → rendered-results journey remains additional coverage. Live bridge acceptance belongs to `SemanticVerifyIntegrationTests.LabHost.cs` and requires the explicitly owned disposable fixture, prepared JS dependencies and sample hosts. It uses `node` on PATH or an explicit `GRAPHBRIDGE_NODE_EXECUTABLE`; prepare this setup before running the full Tooling integration suite.

`eng/query-lab/verify_lab.py` separately checks generated static HTML/PDF reports. Generated captures and receipts stay beneath ignored `.artifacts`; historical maintainer media are not test dependencies.
