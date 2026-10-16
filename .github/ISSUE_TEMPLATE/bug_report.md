---
name: Bug report
about: A reproducible GraphBridge failure
title: "[Bug] "
labels: ''
assignees: ''
---

## Versions and environment

GraphBridge / EF Core / Neo4j.Driver / .NET SDK and runtime / Neo4j version and edition / operating system:

## Minimal reproduction

Provide a small synthetic model, mappings, seed and exact command or LINQ query. State tracking mode and whether an explicit transaction is active. Include steps another developer can run on a disposable graph.

## Expected and actual result

Include the exception type and sanitized message. If a write failed, distinguish a confirmed rollback from an unknown commit outcome.

## Generated Cypher and evidence

Include `ToQueryString()` if translation succeeds, relevant test/run links and sanitized logs. Never attach passwords, tokens, connection environment files, production records or raw parameter values. Replace sensitive values with synthetic examples.

## Scope check

Link the applicable compatibility/limitation entry and explain whether this behavior is documented as supported.
