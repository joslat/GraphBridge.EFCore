# Opt-in Enterprise restricted-user acceptance

This is a separate, disposable Enterprise harness. It does not change the Community fixture, accept a license, or run in normal tests. The user-authorized evaluation run 38125489524 passes all seven tests, retains exact privilege rows and removes the owned fixture. Recorded grants (maintainer-local record) preserve the nonsecret evidence. This verifies this dedicated runtime profile only.

The scope is a whole dedicated application database (`neo4j`), **not tenant isolation**. Full graph `MATCH` visibility is intentional: native-reference diagnostics must see every incoming relationship, including malformed labels or duplicate parents.

## Explicit prerequisites

- A person authorized to accept the applicable Neo4j terms must choose **`yes` (commercial)** or **`eval` (evaluation)**. Neither has a default. See [Neo4j licensing](https://neo4j.com/terms/licensing/) and [Enterprise terms](https://neo4j.com/terms/enterprise_us/). Do not substitute agent inference for that choice.
- Select `-Edition enterprise` explicitly and supply the exact approved image below. The scripts reject other tags/digests and inherited license environment defaults.
- Coordinate all tests/builds with the primary agent. The Enterprise runner has an exclusive operation lock, but it cannot serialize independently invoked Community commands or work in other repositories.
- Reserve 5 GiB available host RAM plus the 2 GiB container limit and 512 MiB startup headroom. The existing explicitly opted-in fresh GitHub-hosted policy reserves 2 GiB instead. Windows/Linux available-memory measurement must succeed.
- PowerShell 7+ is required by supervised startup and tests. They check memory every five seconds and terminate only their owned child process tree on failure. A failed startup stops its newly created, verified server. After failed tests, an idle owned server is stopped if the reserve remains breached or cannot be measured. The environment is retained for dedicated teardown; other developers' resources are never stopped.

Verified image metadata (no image pull or license acceptance was needed to inspect it):

```text
neo4j:2026.09.0-enterprise@sha256:595846b50b04d5f93a4850f501c6105412dc3ea7b6594126d361720b0f450a52
```

The pinned [Enterprise entrypoint](https://github.com/neo4j/docker-neo4j-publish/blob/32e4b7cfe361a4d8a6debf7bc1a6ed8d71507356/2026.09.0/trixie/enterprise/local-package/docker-entrypoint.sh) accepts literal `yes` or `eval` and otherwise refuses Enterprise startup. This harness preserves that explicit choice.

## Run only after approval

1. Restore/build `tests/GraphBridge.EnterpriseIntegrationTests/GraphBridge.EnterpriseIntegrationTests.csproj` through the primary agent's serialized resource checks. Runtime test execution uses `--no-restore`; `-NoBuild` also disables rebuilding.
2. Invoke `Start-EnterpriseFixture.ps1` with all three parameters: `-Edition enterprise`, `-LicenseAcceptance` followed by the explicitly approved choice, and `-Image` followed by the exact image above. This is the only step that can pull/start the Enterprise image.
3. Run `pwsh eng/enterprise/Run-EnterpriseTests.ps1 -NoBuild`.
4. Run `pwsh eng/enterprise/Stop-EnterpriseFixture.ps1` to remove only the validated owned Compose project.

The fixture uses different loopback ports (`18687` Bolt, `18474` HTTP), a random Compose identity, random 256-bit admin/runtime credentials, tmpfs data/logs, and an admin-written `GraphBridgeEnterpriseFixture` marker. Credentials live only in the ignored `.artifacts/enterprise-fixture/fixture.env` and process/container environments. Scripts do not print them or pass passwords in host command arguments. On Unix the generated credential file is mode 0600. Do not publish that file or Docker environment inspections.

The fixture verifies the marker, exact server version and Enterprise edition **before** setup writes. Admin precreates application tokens and uniqueness constraints. Only this fresh owned fixture's four default `PUBLIC` grants are revoked; the harness then requires `PUBLIC` to be empty. Existing unexpected grants cause failure, not silent acceptance. Effective user privileges and original `PUBLIC` grants are emitted as secret-free test metadata and retained in the runner log.

## Reviewed runtime profiles

| User | Application database grants | Purpose |
| --- | --- | --- |
| `gb_reader` | `ACCESS`, full node/relationship `MATCH`, `SHOW CONSTRAINT` | EF reads and native-reference Include |
| `gb_writer` | Reader grants plus `WRITE` | Node/native-reference/identified-edge CRUD |
| `gb_no_schema` | `ACCESS`, full `MATCH`, `WRITE`; no `SHOW CONSTRAINT` | Prove schema observation fails before provider mutations |

No runtime profile receives schema management, token creation, user/role administration, procedures, functions, or external `LOAD` grants. Built-in Cypher functions remain usable. The audit validates exact grants, resources, graph, segments, role ownership, and absence of unexpected roles; default administrative accounts are not runtime credentials.

The seven passing tests cover parameterized reader queries and Include; writer create/update/delete of nodes, owned native references and identified edges; exact `Neo.ClientError.Security.Forbidden` save failures with unchanged EF pending state and no stored changes; missing constraint visibility; denied schema/user administration and token creation. Denial probes use explicit rollback transactions even if unexpectedly permitted. Cleanup uses random run IDs and plain relationship/node `DELETE`, never `DETACH DELETE` or global graph reset.

Authorization syntax comes from the official [privilege administration](https://neo4j.com/docs/operations-manual/current/authentication-authorization/manage-privileges/) and [database privilege](https://neo4j.com/docs/operations-manual/current/authentication-authorization/database-administration/) documentation. Syntax, exact effective privilege representation and live denial behavior are verified against the pinned Enterprise server in that run. Enterprise schema deployment, Graph Types, clustering and tenant isolation remain outside this acceptance.

## Optional manual GitHub-hosted acceptance

enterprise-acceptance.yml (retained maintainer evidence) prepares the same harness for a fresh GitHub-hosted Ubuntu VM. It has only a manual `workflow_dispatch` trigger, `contents: read` permission and a 20-minute job timeout. It does not run on push, pull request or a schedule. Creating or reviewing this workflow does not accept terms or establish live acceptance.

An authorized person must supply the required string input `license_acceptance` with exactly `yes` for their applicable commercial entitlement or `eval` for their authorized evaluation. There is no default. The first step rejects empty, differently cased or other values, and the start step validates again before passing that choice to the existing guarded startup script. The workflow cannot determine whether the person has the necessary authority or entitlement; that remains the explicit prerequisite above.

After reviewing the chosen ref and making that authorized choice, substitute both placeholders in this invocation. The placeholder itself is not a license choice:

```powershell
gh workflow run enterprise-acceptance.yml --ref '<reviewed-ref>' --field 'license_acceptance=<approved-choice>'
```

The workflow must be available on the repository's default branch for manual dispatch. Use a reviewed branch or tag for `<reviewed-ref>` and the explicitly approved literal for `<approved-choice>`; do not infer evaluation acceptance from a request to prepare or review the harness. No dispatch was performed as part of adding these files.

The hosted job uses the repository's exact SDK 10.0.401 pin, locked EF Core 10.0.13 / Neo4j.Driver 6.3.0 dependencies and the Enterprise 2026.09.0 digest above. Restore and build run serially before server startup; the existing test runner uses `-NoBuild` and `--no-restore`. The explicitly opted-in hosted policy reserves 2 GiB plus the existing bounded workload headroom, while local/self-hosted execution retains its 5 GiB reserve. The startup/test scripts continue to enforce resource monitoring and exact fixture ownership.

An `always()` teardown step invokes the existing dedicated stop script whenever `.artifacts/enterprise-fixture/fixture.env` exists, including after failed startup or tests. That script validates ownership before removing resources; abrupt runner termination can still prevent an Actions cleanup step from executing. No artifact upload is configured: credentials, environment files and the entire fixture directory must not be published. Review the secret-free test output in the job log and record the actual run/ref/results in the implementation ledger. Run 38125489524 completed this acceptance after the user explicitly selected evaluation; future executions still require their own applicable explicit license choice.
