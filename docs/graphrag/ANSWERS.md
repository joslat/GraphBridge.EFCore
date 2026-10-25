# Answer with evidence: GraphRAG you can trust

`IGraphRagAnswers.AnswerAsync(question)` asks the chat client for an answer and the exact supporting quote for each claim. The answer then has one of two dispositions:
- **`Answered`:** text with citations, each of which has been located in the stored text;
- **`InsufficientEvidence`:** a refusal with a reason, and no answer text.

## How citations are checked

Each quote must be found in the evidence item it names. Only these differences are forgiven:
- runs of whitespace;
- straight and typographic quotation marks and apostrophes;
- hyphens and dashes;
- at most two `...` elisions, in order.

Nothing else is forgiven: no fuzzy matching, no case folding, no match across chunks. A citation records the stored text and its exact byte span, not the model's copy. If any citation fails, or none supports the answer, the result is a refusal.

## When the answer refuses

| Reason | Meaning |
|---|---|
| `no-evidence` | Retrieval found nothing |
| `model-declined` | The model said the evidence doesn't answer the question |
| `unreadable-response` | The reply wasn't the JSON asked for |
| `no-citations` | The model answered without citing anything |
| `citation-not-found` | A quote isn't in the evidence it names |

Context passages, such as neighbours and Cypher expansions, help the model read. They are never cited.

## Receipts

Every answer carries a `Receipt`:
- its chat calls;
- input and output tokens, when the client reports them;
- the time spent retrieving and answering.

## What isn't guaranteed

- **The truth of the sources.** A citation proves that the stored text says something, not that it is right.
- **Completeness.** Retrieval returns the best-ranked evidence within the budget. Something the budget left out can't be cited.
- **That the answer follows from its quotes.** The quotes are real; whether they support the answer's wording is for an evaluation, or for a reader, to judge.
- **Exact scores at large limits.** Neo4j's vector indexes are approximate, so near-ties can reorder; never rely on the order of near-equal scores.

Extracted facts and mentions follow the same rule: every mention and quote is located in its chunk before it is stored, and a fact is kept only when both of its ends are grounded in the same chunk.
