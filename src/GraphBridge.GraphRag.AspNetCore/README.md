# GraphBridge.GraphRag.AspNetCore

**Experimental.** ASP.NET Core endpoints for the [GraphBridge GraphRAG toolkit](https://github.com/joslat/GraphBridge.EFCore/tree/main/src/GraphBridge.GraphRag). The API may change in any preview; using it reports `GBRAG001`.

**Secure them yourself.** The endpoints read your documents and call your model, so they need your authentication, authorization and rate limits. `MapGraphRag` returns the route group for that:

```csharp
builder.Services.AddGraphRag<AppDbContext>(new GraphRagEmbeddingSet("text-embedding-3-small", 1536));   // with an IChatClient and an IEmbeddingGenerator registered

app.MapGraphRag<AppDbContext>("/graphrag")
    .RequireAuthorization()
    .RequireRateLimiting("graphrag");
```

## Endpoints

- **`POST /graphrag/ask`** with `{"question": "..."}` streams [server-sent events](https://html.spec.whatwg.org/multipage/server-sent-events.html):

  | Event | Data |
  |---|---|
  | `retrieved` | how many evidence and context items were found, their size, what the budget left out, and the trace ID when tracing is on |
  | `delta` | the next piece of the answer as the model writes it. **Provisional:** no citation has been checked yet |
  | `verified` | the answer, which replaces the provisional text, and its citations: document, chunk, byte span and the stored quote |
  | `refused` | the reason; `withdraws` is true when provisional text was sent and must be removed |
  | `error` | `retrieval-failed` or `model-failed`; the details are in your log |

  Show `delta` text as provisional, and replace or remove it when `verified` or `refused` arrives. Closing the request stops the model call.

  For a follow-up, send the conversation so far as well: `{"question": "...", "history": [{"question": "...", "answer": "..."}]}`, oldest first, at most 20 turns. The server keeps no conversation. It rewrites the follow-up into a standalone question, and `retrieved` carries `rewrite`: the question asked and the one retrieved ([follow-up questions](../../docs/graphrag/ANSWERS.md#follow-up-questions)).
- **`POST /graphrag/search`** with `{"query": "...", "limit": 5}` returns up to 20 passages, each with its document, chunk, byte span and text. Passages are evidence to read, not answers.
- **`GET /graphrag/documents/{id}`** returns a document's metadata and its chunks' byte spans, in order.

`Retrieval` sets the retrieval options for `ask` and `search`, checked on each request; without it, the defaults given to `AddGraphRag` apply. `ScopeFor` sets each request's scope from your application, for example the signed-in user's tenant; the caller can't choose it. It applies to all three endpoints: `documents/{id}` answers 404 for a document outside the scope, and lists only the chunks inside it. An empty question, or one longer than `MaxQuestionLength` (2,000 characters by default), gets a 400 response before any work. Nothing is written or cached.

Requires Neo4j 2026.09 or later. Design: ADR 0052, section 12, in the repository's `docs/architecture/adr`.
