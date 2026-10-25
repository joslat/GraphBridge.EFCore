# Security policy

## Reporting a vulnerability

Please report security problems privately through GitHub: open the repository's [Security tab](https://github.com/joslat/GraphBridge.EFCore/security) and choose **Report a vulnerability**, or go straight to [a new private report](https://github.com/joslat/GraphBridge.EFCore/security/advisories/new). Do not open a public issue, discussion or pull request for a suspected vulnerability.

Include what you can of:

- the affected package and exact version (for example `GraphBridge.EntityFrameworkCore 0.1.0-preview.5`), with the .NET, EF Core, Neo4j.Driver and Neo4j versions;
- the model, query or call that shows the problem, reduced to a small synthetic example;
- the generated Cypher, if a query is involved, and what an attacker could read, change or cause.

Never include credentials, connection strings, tokens or real data. Use placeholders and synthetic values.

GraphBridge is maintained by one person as an open-source developer preview. Reports are read and answered as quickly as possible, and fixes ship as a new preview release with a GitHub security advisory that credits the reporter, unless you prefer otherwise.

## Supported versions

GraphBridge is a developer preview and is not production-supported. Security fixes go into the next preview only; earlier previews are not patched. Upgrade to the latest `0.1.0-preview` release to receive fixes.

| Version | Security fixes |
|---|---|
| Latest `0.1.0-preview` | Yes, in the next preview |
| Earlier previews | No |

## Scope

In scope: the packages published from this repository (`GraphBridge.EntityFrameworkCore`, `GraphBridge.Tooling`, `GraphBridge.Generators`, `GraphBridge.Templates`), the samples, the workshop and the Query Lab VS Code extension source.

Out of scope, please report upstream:

- Entity Framework Core: [dotnet/efcore security policy](https://github.com/dotnet/efcore/security/policy)
- The Neo4j .NET driver and the Neo4j server: [Neo4j security](https://neo4j.com/security/)

## What GraphBridge guarantees

These properties are part of the design; a way around any of them is a vulnerability worth reporting:

- In Cypher that GraphBridge generates, query values are always parameters, never spliced into statement text, and labels, relationship types and property names come only from the validated EF model. Native Cypher you write yourself takes a parameter dictionary: pass values there, never in the statement text.
- A query shape that cannot be translated faithfully is refused before any database call; there is no client-side evaluation fallback.
- GraphBridge never creates or changes schema implicitly; schema changes are reviewed proposals or explicit migration plans.
- Doctor and Verify reports exclude parameter values, and command tracing does not log values.
