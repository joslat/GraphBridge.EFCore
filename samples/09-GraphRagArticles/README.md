# 09 · GraphRAG articles

Extract the people, organisations, places, events, concepts and topics of news articles into a typed graph: who wrote each article, what it is about, who works where, and who said what. The sample uses the experimental [GraphRAG toolkit](../../src/GraphBridge.GraphRag/README.md) with its standard article ontology.

**The two articles in `data/articles` are fictional, written for this sample.** Larkfield, its library and its people are invented. The sample reads any folder of Markdown articles. Their title, publication date, publisher and URL come from `articles.json` next to the folder, as the source's own metadata; they are never extracted.

It runs offline, with extraction replies written by hand for the two articles, or with your own model through an OpenAI-compatible endpoint, configured as in [sample 08](../08-GraphRagNovel/README.md). The real-model path hasn't been run yet.

Point the four `GRAPHBRIDGE_NEO4J_*` variables at a disposable Neo4j 2026.09 or later database, then:

```text
dotnet run --project samples/09-GraphRagArticles -- setup
dotnet run --project samples/09-GraphRagArticles -- ingest
dotnet run --project samples/09-GraphRagArticles -- report
dotnet run --project samples/09-GraphRagArticles -- ask "What did Reyes say about seeds?"
dotnet run --project samples/09-GraphRagArticles -- serve
dotnet run --project samples/09-GraphRagArticles -- cleanup
```

- **`setup`** applies the schema plan as a migration.
- **`ingest`** reads the articles. Facts stated by both articles, such as Tomas Reyes working for the library, are stored once and list both articles.
- **`report`** prints, through typed LINQ over `Authored`, `About`, `WorksFor`, `LocatedIn<Organization>` and `Quoted`:
  - each article with its authors and topics;
  - who works where, and in how many articles that is stated;
  - which organisations are where;
  - every quotation with its speaker.
- **`ask`** answers with checked citations, or refuses.
- **`serve`** starts a small ASP.NET Core endpoint inside the sample, with no extra package. It defaults to `http://localhost:5179` and serves `GET /ask?q=...` and `GET /report`.
- **`cleanup`** removes the articles and the things no longer mentioned anywhere.

The ontology's labels are prefixed with `GraphRagOntologies.Article.WithLabelPrefix("News")`, so `NewsPerson` and `NewsPlace` don't collide with sample 08's `Place` and `Event` in a shared database. The kinds shown to the model and the relationship types stay the same.
