# GraphBridge.GraphRag.AgentFramework

**Experimental.** A [Microsoft Agent Framework](https://www.nuget.org/packages/Microsoft.Agents.AI.Abstractions) context provider for the [GraphBridge GraphRAG toolkit](https://github.com/joslat/GraphBridge.EFCore/tree/main/src/GraphBridge.GraphRag). The API may change in any preview; using it reports `GBRAG001`.

`GraphRagContextProvider` gives an agent the toolkit's tools on every invocation:
- `answer_from_documents` and `search_documents`;
- `find_things` and `related_facts`, when your model maps an ontology.

Its instructions tell the agent to answer questions about the documents only through `answer_from_documents`. That tool's citations are checked against the stored text, so the agent cites only what is stored, or says the documents don't answer.

```csharp
var provider = GraphRagContextProvider.Create<AppDbContext>(services, new GraphRagToolOptions { Trail = trail });
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [provider] });
var response = await agent.RunAsync("Who works for the library?");
// trail.Calls records each tool call with the document spans it returned.
```

Requires Neo4j 2026.09 or later. Design: ADR 0052, section 8, in the repository's `docs/architecture/adr`.
