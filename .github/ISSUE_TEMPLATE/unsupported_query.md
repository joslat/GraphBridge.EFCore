---
name: Unsupported graph query
about: A useful LINQ or Fluent Cypher shape is rejected
title: "[Unsupported query] "
labels: ''
assignees: ''
---

## Intended result

Describe the graph question and why existing LINQ, Fluent Cypher or explicit native Cypher does not meet it. Link the current supported contract/limitation where possible.

## Exact environment

GraphBridge package/version, EF Core, Neo4j.Driver, .NET SDK/runtime, operating system, Neo4j version/edition and selected database profile. Include resolved versions if they differ from requested versions.

## Minimal synthetic reproduction

Provide small entity/mapping definitions, synthetic seed rows/edges and the exact LINQ or Fluent query. Include direction, optional edges, nulls, duplicate/parallel edges, expected ordering and row limits where relevant. State expected and actual results or the phase that rejects the query.

## Sanitized diagnostic evidence

Include the stable diagnostic code when present, exception type, compile/execute phase and generated Cypher from `ToQueryString()` if compilation succeeds. If it fails before a query exists, say so; do not invent generated output. A small handwritten native reference query and expected result can help establish semantics.

Do not attach credentials, connection strings, fixture environment files, production data, raw parameter values, unreviewed plan arguments or full unsanitized logs. Generated query text and model/schema identifiers can also be sensitive; replace domain names with synthetic equivalents while preserving the failing shape.

## Scope and alternatives tried

Is this a documented unsupported operation, a regression of supported behavior, or an unclear mapping/permission failure? State any successful native alternative and the smallest additional shape that would unblock the application. This report is a regression candidate, not an automatic support commitment.
