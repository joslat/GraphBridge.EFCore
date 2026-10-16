# Movies fixture provenance

This is an original, small GraphBridge fixture authored for database-first development. It uses the familiar person/movie/acting/directing graph shape, but does not copy Neo4j's Movies tutorial dataset, tutorial Cypher, real actor records, film descriptions or dialogue. All people, movie titles, roles and descriptive text in the parameter file are fictional sample content; any resemblance to real people or works is incidental.

The fixture contains three people, two movies, four acting credits and one directing credit. Two separately identified acting credits connect the same person and movie. Their native `roles` string lists deliberately exercise payload preservation and the distinction between an independently identified relationship and a unique endpoint pair.

Application IDs are explicit canonical GUID strings authored in `source/movies.fixture.parameters.json`. They are not derived from names, titles, row positions or Neo4j `elementId()`. No database export, production data, external download, inference service or secret is required to reproduce this input.

`source/movies.fixture.cypher` is parameterized source for creating this exact dataset in the repository's owned disposable fixture. The reviewed schema proposal and mapping policies are separate artifacts, so the data can first be inspected without assuming constraints or tracking eligibility. No execution or compatibility claim follows from these source files alone; actual test evidence belongs in the implementation tracker.
