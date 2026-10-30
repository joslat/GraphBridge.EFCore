# Ontologies

An ontology says what extraction looks for and how it is stored. Pass one to `AddGraphRag`, and ingestion extracts each chunk through an `IChatClient`:

```csharp
modelBuilder.AddGraphRag(1536, o => o.Ontology = GraphRagOntologies.Article);
```

## Where it is used

- **Extraction:** the model gets short instructions and a JSON schema read from your finalized EF model (`GraphRagModelSchema`).
- **Checking:** anything outside the ontology is pruned, with a reason in the receipt.
- **Storage:** each class is a label, each fact a relationship type, each mention a relationship to the chunk with the words' exact bytes.
- **Queries:** the classes are ordinary EF entities, so typed LINQ reads them.

## The three standard ontologies

**Generic document** (`GraphRagOntologies.Generic`), for any text.

| Things | Facts |
|---|---|
| `Person`, `Organization`, `Place`, `Event` (with the text's date), `Concept` | `WORKS_FOR` (role), `MEMBER_OF`, `LOCATED_IN`, `PARTICIPATES_IN` (role), `PART_OF`, and `RELATED_TO` (description) between any two |

**Novel** (`GraphRagOntologies.Novel`), for fiction.

| Things | Facts and quotes |
|---|---|
| `Character`, `Place`, `Group`, `Event` (with its chapter) | `KIN_OF` (kind), `RELATED_TO` (kind, description), `MEMBER_OF`, `PARTICIPATES_IN`, `TAKES_PLACE_IN`; `SAYS`: who speaks which words, to whom |

**Article** (`GraphRagOntologies.Article`), for news, posts and papers. It has the generic document's classes and facts, plus:
- `Topic`;
- `AUTHORED`: a person to the article;
- `ABOUT`: the article to a topic;
- `QUOTED`: who is quoted saying which words.

The article's published date, publisher and URL come from the source's own metadata (`GraphRagSourceMetadata`), never from extraction.

## How things are stored

- **Things** are nodes keyed by a hash of their kind and normalised name. The same name of the same kind is one thing across chunks and documents.
- **Placeholders** such as "the man" or "someone" never merge: each gets an ID from its own span, and `IsPlaceholder` is true.
- **Mentions** are identified relationships `MENTIONED_IN_<CLASS>` from the thing to the chunk, with `StartByte`, `EndByte` and the `Quote`.
- **In neo4j-graphrag's shape,** `ontology.WithNeo4jGraphRagEntities()` stores things with its `__Entity__` and `__KGBuilder__` labels, every mention as `FROM_CHUNK` and the name as `name`, so its retrievers read them ([entities](INTEROP.md#one-graph-two-stacks-entities)).
- **Facts** are association edges with the ontology's type names, one per pair of things. Each lists the documents that state it (`DocumentIds`).
- **Quotes** (`SAYS`, `QUOTED`) are identified relationships from the speaker to the chunk, with the words' exact bytes.

```csharp
var characters = await db.Set<Character>().Where(c => !c.IsPlaceholder).OrderBy(c => c.Name).ToListAsync();
var kin = await db.Set<KinOf>().ToListAsync();
var lines = await db.Set<Says>().ToListAsync();
var located = await db.Set<LocatedIn<Organization>>().ToListAsync();
```

## Your own ontology

```csharp
var ontology = GraphRagOntology.Custom("lab", b => b
    .Class<Person>("A researcher.")
    .Class<Place>("A lab or a city.")
    .Relationship<LocatedIn<Person>, Person, Place>("LOCATED_IN", "Where the researcher works.")
    .Placeholders("the researcher"));
```

Your classes derive from `GraphRagEntity`, `GraphRagFact` and `GraphRagQuote`. If your model already uses a label such as `Person`, use `ontology.WithLabelPrefix("Rag")`. It stores `RagPerson` with `MENTIONED_IN_RAG_PERSON`, while the kinds shown to the model and the fact types stay the same.

The schema steps for an ontology add:
- a constraint on each class and on each mention and quote type;
- one full-text index over every class's name and aliases.

Get them with `GraphRagSchema.CreateSteps(layout, set, ontology)`.

## Try it

- [Sample 08](../../samples/08-GraphRagNovel/README.md) extracts a novel with the novel ontology, then reads characters, kin and who says what through typed LINQ.
- [Sample 09](../../samples/09-GraphRagArticles/README.md) uses the article ontology: authors, topics, jobs and quotations, with facts that list every article stating them.
