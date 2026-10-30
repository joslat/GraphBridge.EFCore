# Office-document recipe: markitdown

The opt-in test `MarkItDownRecipeTests` in `tests/GraphBridge.GraphRag.InteropTests` runs the [Office documents](../../../docs/graphrag/AGENTS.md#office-documents) recipe: Word files read by Microsoft's [markitdown](https://github.com/microsoft/markitdown) through DataIngestion's `MarkItDownReader`, written by `GraphBridgeChunkWriter` with their text's origin, then retrieved and answered. The test builds its Word files itself; nothing is downloaded but the tool.

## Run it

Create an isolated environment, install the pinned packages, and point the test at the tool:

```text
python -m venv .artifacts/markitdown-venv
.artifacts/markitdown-venv/Scripts/python -m pip install --require-hashes -r tests/interop/markitdown/requirements.txt
$env:GRAPHBRIDGE_MARKITDOWN = (Resolve-Path .artifacts/markitdown-venv/Scripts/markitdown.exe).Path
./eng/scripts/Start-Neo4jFixture.ps1
./eng/scripts/Run-IntegrationTests.ps1 -Suite Interop -Filter "FullyQualifiedName~MarkItDownRecipeTests"
```

On Linux the tool is `.artifacts/markitdown-venv/bin/markitdown`. `requirements.txt` pins markitdown 0.1.9 with its `docx` extra and their required dependencies, with the hash of every file published for each version. Without `GRAPHBRIDGE_MARKITDOWN`, the test is skipped.
