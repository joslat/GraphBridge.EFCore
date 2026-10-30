# Interop suite: GraphBridge and neo4j-graphrag

The Python half of `tests/GraphBridge.GraphRag.InteropTests`. The .NET tests run `interop.py` with a JSON request on standard input and read a JSON result. It drives Neo4j's [neo4j-graphrag](https://github.com/neo4j/neo4j-graphrag-python) 1.22.0 on the same disposable database GraphBridge uses. No model is called: both stacks embed with the same word-hashing function, and a test checks that they produce the same vectors.

## Run it

Create an isolated environment, install the pinned packages, and point the tests at its Python:

```text
python -m venv .artifacts/interop-venv
.artifacts/interop-venv/Scripts/python -m pip install --require-hashes -r tests/interop/python/requirements.txt
$env:GRAPHBRIDGE_INTEROP_PYTHON = (Resolve-Path .artifacts/interop-venv/Scripts/python.exe).Path
./eng/scripts/Start-Neo4jFixture.ps1
./eng/scripts/Run-IntegrationTests.ps1 -Suite Interop
```

On Linux the environment's Python is `.artifacts/interop-venv/bin/python`. `requirements.txt` pins neo4j-graphrag and its required dependencies, with the hash of every file published for each version. Without `GRAPHBRIDGE_INTEROP_PYTHON`, the tests are skipped. To run the suite on a fresh GitHub-hosted machine instead, start the manual **GraphRAG interop with neo4j-graphrag** workflow (`.github/workflows/interop.yml`).

## What the library writes

Read from neo4j-graphrag 1.22.0's source and checked by these tests:
- **Labels:** every node its writer creates also gets `__KGBuilder__`, the documents and chunks included; extracted things get `__Entity__` as well, and link to their chunk with `FROM_CHUNK`. The `entities` command reads a GraphBridge graph written in that shape (`WithNeo4jGraphRagEntities`) with the library's `VectorCypherRetriever`.
- **No chunk ID:** chunks are written with `text`, `index` and `embedding`. The writer keeps an ID only while it writes, as `__tmp_internal_id`, and removes it afterwards. Its reader looks for `id`, which GraphBridge writes.
- **Documents** have `path` and `createdAt`, and any metadata, but no ID.
- **APOC:** the writer creates relationships with `apoc.merge.relationship`. Without the APOC plugin, it writes the nodes and reports `FAILURE` for the relationships, without raising. The Neo4j image ships APOC core in its `labs` folder; `NEO4J_PLUGINS='["apoc"]'` enables it. The suite's database has no APOC, so `interop.py` uses the library's writer with that one call replaced by `MERGE` with a dynamic relationship type.
