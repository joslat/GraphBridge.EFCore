# 32 · Things, facts and quotes

**Goal:** extract the characters and places of a passage, a fact between them and who says what, each checked against the passage before anything is written, and see what the checks prune.
**Track:** full · **Mode:** offline + Neo4j 2026.09 or later · **Time:** ~15 min · **Builds on:** 31

> The GraphRAG toolkit is **experimental** (`GBRAG001`): its API may change in any release.

## Concepts

- An **ontology** says what a model may extract: classes of things (a character, a place), the facts allowed between them (`KIN_OF` from a character to a character), and quote types (`SAYS`, from a speaker). The toolkit ships three, the generic document, the article and the novel, and builds your own with `GraphRagOntology.Custom`.
- A model's reply names each thing with the **exact words** that mention it. The toolkit finds those words in the passage and stores their byte span, so a mention can always be shown in its source.
- Before anything is written, every item is **checked**: words that aren't in the passage, a fact the ontology doesn't allow, a thing nothing in the passage mentions. What fails is pruned, and the receipt says why. Nothing is repaired by guessing.
- The things, facts and quotes are ordinary EF Core entities: LINQ reads them.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 32
```

Offline, the lesson shows the ontology. With a database, it ingests one passage, using a reply written by hand in place of a model, so the run needs no key.

## Walkthrough

1. **The ontology.** `WithLabelPrefix` stores the novel's classes under labels of the workshop's own, `WorkshopCharacter` and `WorkshopPlace`, so they never meet the movie graph's `Person`. The kinds the model sees don't change.

<!-- region: Ontology -->
```csharp
var ontology = GraphRagOntologies.Novel.WithLabelPrefix("Workshop");
```

2. **Ingest and check.** The written reply has three mistakes on purpose: the Cheshire Cat isn't in the passage, `TAKES_PLACE_IN` joins an event to a place, not a character to one, and the White Rabbit says nothing here. The receipt lists four pruned items, because the Cat's mention and the Cat both fail.

<!-- region: Extract -->
```csharp
var receipt = await services.GetRequiredService<IGraphRagIngestion>()
    .IngestAsync([GraphRagSources.Text("riverbank", Passage)], cancellationToken);
var extraction = receipt.Extraction!;
```

3. **Read it back.** Three characters, each mention at its bytes, the `KIN_OF` fact with its property, and Alice's question with the person she asks.

<!-- region: Read -->
```csharp
await using var scope = services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<GraphRagWorkshop.RagContext>();
var characters = await db.Set<Character>().OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(cancellationToken);
var mentions = await db.Set<GraphRagMention<Character>>().ToListAsync(cancellationToken);
var kin = await db.Set<KinOf>().SingleAsync(cancellationToken);
var says = await db.Set<Says>().SingleAsync(cancellationToken);
```

The lesson removes everything it wrote at the end: documents, chunks, things, runs, leases and its schema.

## Try it yourself

- Add a mention in the reply that isn't in the passage, such as `"the Hatter"` for Alice, and watch it pruned while Alice stays.
- Change `"sibling"` to another word: `KIN_OF`'s kind isn't checked against a list, so it is stored as given. That is what "the words are in the passage" doesn't prove.

## Limits

- The checks prove that the words are in the passage, not that the model read them right: a wrong alias or relation that quotes real words passes. The [ingestion guide](../../../../docs/graphrag/INGEST.md#extraction) lists every check and reason.
- Names resolve by exact match, ignoring case and spacing, so one character can arrive under two names; lesson 34 merges them.
