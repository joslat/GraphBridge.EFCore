# Graph retrieval

A graph database stores things as nodes and the links between them as relationships. Retrieval finds the passages that answer a question.

## Two routes

The vector route compares the meaning of the question with the meaning of each passage. The full-text route finds passages that contain the question's words.

Reciprocal rank fusion merges the two lists into one ranking, so a passage that both routes find rises to the top.

## Checked citations

Every answer quotes the passages it rests on. Each quote is located in the stored text, at its exact bytes, before the answer is returned; a quote that isn't there makes the answer a refusal.
