# Troubleshooting

GraphBridge borrows your application's `IDriver`. It does not choose credentials, repair the server, create a database or create schema. Find your symptom below; for a model or query problem, start with offline [Doctor](../tools/DOCTOR.md).

## Connection troubleshooting

| Symptom | Check | Action |
|---|---|---|
| Connection refused, service unavailable | Server health, host and Bolt port | Use the Bolt endpoint (default port 7687; the repository's disposable fixture uses `127.0.0.1:17687`). The HTTP port (7474, or 17474 for the fixture) is not a driver endpoint. Wait until the server reports it is ready |
| Authentication failure | Username and password for this server | The repository fixture generates new credentials each time it is recreated; reload them through its scripts. Never paste credentials into an issue, and do not disable authentication to hide a bad configuration |
| TLS handshake or certificate error | URI scheme and the server's certificate | Use the scheme your deployment requires: `+s` needs a trusted CA certificate, `+ssc` accepts a self-signed one. Do not switch TLS off as a workaround. The local fixture uses plain `bolt://` |
| Database unavailable or not found | The database name passed to `UseNeo4j` and the account's access | `UseNeo4j(driver)` selects `neo4j` unless you pass another name; a server's home database can differ. GraphBridge never creates databases |
| Login works but writes fail | Account privileges and key constraints | A reader cannot save. Writers also need to see constraints, because every save checks its key constraints. See the [verified Enterprise profiles](../../eng/enterprise/README.md). Authentication is not authorization |
| Fixture marker, host or edition rejected | You are running repository tests, samples or the workshop | They accept only their own disposable database. Use the repository scripts and their fixture; do not remove the guards or point tests at someone else's database |
| Fixture startup refused for memory | Available host RAM | The repository scripts keep 5 GiB free plus fixture headroom. Stop only your own idle fixtures, or try later |
| Schema error after a successful connection | Required uniqueness constraints | Inspect and deploy the reviewed constraints ([schema and migrations](../features/SCHEMA_AND_MIGRATIONS.md)). Reconnecting or retrying does not fix missing constraints |

Neo4j documents [URI schemes, routing, encryption and authentication](https://neo4j.com/docs/dotnet-manual/current/connect-advanced/). Driver trace output can contain sensitive data. `ToQueryString()` contains no parameter values, but application logs, driver logs and exception text are not automatically safe to share.

When you report a problem, include exact versions, the sanitized URI scheme and port, the exception type or error code, and a synthetic reproduction. Leave out credentials, tokens, private hostnames and real records. A connection lost during a commit has an uncertain outcome: follow [recovering a failed graph operation](../features/TRANSACTIONS_AND_CONCURRENCY.md#recovering-a-failed-graph-operation) instead of repeating the write.

## Common exceptions

| Exception | Cause | What to do |
|---|---|---|
| `Neo4jQueryTranslationException` with a `GBQ` code | A LINQ shape outside the supported subset | See [refusal codes](KNOWN_LIMITATIONS.md#refusal-codes) for the alternative |
| `FluentCypherException` (a `NotSupportedException`): "GraphBridge fluent Cypher [GBFC...]" | A Fluent query GraphBridge refuses; the code is in `Code` and in `Exception.Data["GraphBridge.FluentDiagnostic"]` | See the codes in [Fluent Cypher](../features/FLUENT_CYPHER.md#predicates-as-c-lambdas) |
| `NotSupportedException` asking for async operations | `SaveChanges()`, `ToList()`, synchronous transaction calls | Use the async API and `await using` |
| `InvalidOperationException`: "GraphBridge requires an existing uniqueness constraint" | A key constraint is missing | Deploy the reviewed constraint ([schema and migrations](../features/SCHEMA_AND_MIGRATIONS.md)) |
| `DbUpdateConcurrencyException` | Someone else changed or deleted the element since you loaded it | Reload in a fresh context and decide ([optimistic concurrency](../features/TRANSACTIONS_AND_CONCURRENCY.md#optimistic-concurrency)) |
| `DbUpdateException` saying the commit outcome may be unknown | The commit failed or its response was lost | Do not retry blindly; check durable state first ([recovery](../features/TRANSACTIONS_AND_CONCURRENCY.md#recovering-a-failed-graph-operation)) |

## Doctor decision tree

Start with [offline Doctor](../tools/DOCTOR.md#registered-queries-and-offline-doctor). It separates model and query problems from database observations and needs no credentials. Move to connected inspection only when you intend to read metadata from the selected database. A report is evidence for the registered scope, not a deployment certificate.

| Observation / code | What to do next | What the result does not mean |
|---|---|---|
| `GBD-MODEL-001` | Fix the finalized node/relationship identity, scalar or endpoint mapping using [mapping diagnostics](../tools/MAPPING_GENERATOR.md); rerun offline. | Reconnecting cannot repair the model. Queries left `not-run` were not validated. |
| `GBD-QUERY-001` or `GBD-QUERY-002` | Read the `subcode` (`GBQ` or `GBFC`) and reduce to a supported query shape; inspect `WITH` alias scope, direction and projection. Review [Fluent boundaries](../features/FLUENT_CYPHER.md) and submit a [synthetic unsupported-query repro](https://github.com/joslat/GraphBridge.EFCore/issues/new?template=unsupported_query.yml) if needed. | Unsupported translation is not permission to silently evaluate filters on the client. |
| Offline pass; `databaseObservation: not-run` | Explicitly call connected Doctor if deployment metadata needs checking. | Compilation does not prove credentials, constraints, plans or rows. |
| `GBD-CONN-001` | Check the selected Bolt endpoint, database, TLS and server readiness using the connection table above. | A transport failure says nothing about schema validity. |
| `GBD-CONN-002` | Privately verify the credential and required metadata grants with the database administrator; keep least privilege. | Login success does not guarantee metadata visibility; incomplete inspection cannot pass. |
| `GBD-CAP-001` | Check the observed version and edition against [capability detection](COMPATIBILITY.md#version-and-edition-capability-detection). Use a supported server. | Driver connectivity does not certify every server capability. |
| `GBD-SCHEMA-001` | Inspect grouped finding codes, then privately use [schema inspection](../features/SCHEMA_AND_MIGRATIONS.md) to review the named constraint/index conflict. Apply only an explicitly reviewed schema change. | Doctor never repairs schema, backfills data or runs migrations. |
| `GBD-SCHEMA-002` | Review warnings even when status is pass: optional indexes, application-enforced guarantees and unmanaged schema may need a decision. | Required expectations passing does not eliminate warnings or prove data integrity. |
| `GBD-EXEC-001` | Check cancellation/deadline settings and explicitly rerun the read-only observation when appropriate. | A canceled or unobserved step is not a pass. Do not infer outcomes for unrelated writes. |
| `GBD-EXEC-002` | Inspect the original exception locally; check metadata row limits and unexpected result shapes. Capture only a sanitized repro. | Unknown failure is not healthy metadata or a reason to retry writes. |
| Connected pass | Retain the profile and warnings; use independent semantic tests for result correctness. | Metadata inspection does not execute registered results or establish planability, production resilience or full EF conformance. |

Default JSON and readable text omit parameter values and schema identifiers. Declared public descriptions and source references stay visible, so never put secrets there. Explicit statement disclosure is marked sensitive and needs review before sharing. In automation, keep every required contract's blocked, unsupported or not-run status instead of counting only successful rows.
