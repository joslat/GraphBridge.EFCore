# Synthetic report fixtures

These small JSON contract fixtures were retained from the synthetic Movies sample acceptance on 12 October 2026: Doctor offline/connected, Verify offline/connected/missing-schema/semantic, and two application-host report envelopes. No credentials, connection strings, application parameter values, user data or raw logs are included. Host fixtures retain only report/source fields; redundant presentation copies are excluded.

They exercise supported evidence versions, redaction, schema findings, captured plans, separate reference/query comparisons and malformed-input rejection without requiring a database. Passing a fixture test is **not** a fresh live-database acceptance result. Tests mutate copies to verify integrity guards.

The fresh compiled envelope is generated separately by `prepare.mjs` through the sample's offline `--lab-json` command, exercising current .NET compiler behavior. Live bridge tests continue to execute real V1/V2 operations against an explicitly owned fixture and write separate ignored output. Update these fixtures deliberately when evidence contracts change; do not copy arbitrary customer captures into tests.
