# Application-host capture protocol, version 1

The extension starts an **application-owned, prebuilt .NET executable DLL** with the fixed argument `--lab-host`. It does not load arbitrary assemblies into its own process, build code, discover contexts or interpret C#/Cypher. Application code owns its context, query catalog, database configuration and fixture controls. A trusted host is executable application code, not a sandbox; review it before running it.

Saved-evidence viewing needs no host. Capture requires a trusted local workspace and an explicitly selected DLL whose resolved path is inside that workspace (including symlink resolution). No path or command from a report or webview can start capture. Host operations are VS Code commands, with explicit operation/disclosure selection. Connected inspection and semantic execution require separate confirmation; the sample also enforces its environment/fixture rules.

One process handles one request on standard input, ending at EOF. UTF-8 JSON, at most 4,096 bytes:

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

Operations are `offline` (Verify V0), `inspect` (metadata + EXPLAIN, V1) and `semantic` (V2 owned disposable fixture). These select registered application queries. There are no request fields for query text, parameters, credentials, paths or assembly names. Unknown/duplicate fields and unknown versions are rejected by the sample host.

The host writes one JSON response and exits. A delivered response uses exit code zero even if the **report** describes failed/blocked/unsupported verification. The viewer preserves that distinction. Response:

```json
{
  "formatVersion": 1,
  "requestId": "0123456789abcdef0123456789abcdef",
  "result": { "kind": "graphbridge-query-lab", "formatVersion": 1, "report": {}, "sources": [] }
}
```

Here `report` is a complete .NET report, not the empty illustrative object above. Alternatively, exactly one fixed `error` code replaces `result`: `invalid-request`, `configuration`, `capture-failed`, or `output-limit`. No raw exception text is sent. A malformed request can terminate without a response; an untrusted or mismatched response is never displayed.

The extension limits stdout to 4,000,000 bytes, stderr to 16,384 bytes and elapsed time to 30 seconds. Stderr is not displayed or retained. Cancellation, deadline or overflow terminates the owned process tree and discards partial output. It runs one host at a time, without a shell. The sample cooperatively cancels at 25 seconds and bounds request/response size. Termination is a client resource bound, not proof that remote server work has stopped; the sample uses read-only inspection and rollback verification, with no provisioning or cleanup DDL.

Trusted hosts must not detach background services or leave child processes running after the response. Process-tree termination is best effort under OS permissions; the protocol is not an OS containment boundary. The accepted sample starts no child processes. Do not use arbitrary third-party executables as hosts.

The protocol adds no .NET package dependency. `samples/05-FluentCypher` is its concrete application-owned implementation. A different application must implement the same transport contract and its own reviewed catalog/configuration policy. No generic loader or credential UI is provided.
