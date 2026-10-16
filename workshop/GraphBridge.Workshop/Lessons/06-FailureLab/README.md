# 06 · The failure lab

**Goal:** see which query shapes GraphBridge refuses, and why refusing beats silently running them on the client.
**Track:** ★ core · **Mode:** offline · **Time:** ~10 min · **Builds on:** 05

## Concepts

- GraphBridge translates a **documented subset** of LINQ. Anything outside it throws `Neo4jQueryTranslationException` while the query is compiled.
- It never falls back to loading rows and filtering them in C#. Such a fallback can silently read a whole database.
- Each refusal names the LINQ shape it could not translate, a stable code such as `GBQ101`, and a supported alternative. The code is also in `Exception.Data["GraphBridge.QueryDiagnostic"]`; [known limitations](../../../../docs/reference/KNOWN_LIMITATIONS.md#refusal-codes) lists every code.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 06
```

## Walkthrough

1. **A C# method in a predicate.**

<!-- region: ClientMethod -->
```csharp
lesson.ExpectFailure<Neo4jQueryTranslationException>("a custom C# method in Where is rejected",
    () => db.People.Where(p => LooksFamous(p.Name)).ToQueryString());
```

   The method is never called; the lesson checks its call counter is still zero.

2. **Shapes outside the subset:** grouping, filtering after paging, and `Distinct`.

<!-- region: Shapes -->
```csharp
lesson.ExpectFailure<Neo4jQueryTranslationException>("GroupBy is rejected",
    () => db.Movies.GroupBy(m => m.Released).Select(g => g.Key).ToQueryString());
lesson.ExpectFailure<Neo4jQueryTranslationException>("filtering after paging is rejected",
    () => db.People.Take(5).Where(p => p.Born > 1960).ToQueryString());
lesson.ExpectFailure<Neo4jQueryTranslationException>("Distinct is rejected",
    () => db.Movies.Select(m => m.Released).Distinct().ToQueryString());
```

## What you should see

Five passing checkpoints and the provider's message for each refused shape.

## Try it yourself

1. Rewrite the paging example so it is supported: filter first, then `Take(5)`.
2. Lesson 14 shows a Fluent Cypher refusal (`GBFC110`). LINQ codes start with `GBQ` and Fluent codes with `GBFC`. Why does Fluent refuse `!=` on a nullable property while LINQ translates it?

## Limits

- The supported subset is listed in the compatibility contract; this lesson shows a sample of refusals, not all of them.

## Read more

[Known limitations](../../../../docs/reference/KNOWN_LIMITATIONS.md) · [Compatibility](../../../../docs/reference/COMPATIBILITY.md)
