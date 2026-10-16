// Separate reviewed schema proposal for the original GraphBridge Movies fixture.
// Execute explicitly, one statement at a time, only after reviewing existing metadata.
// These statements are not one atomic multi-statement migration. No automatic deployment occurs.
// Existing names or equivalent constraints must be inspected, not silently adopted.
// Uniqueness does not enforce property existence; approveRequiredKey remains an explicit assumption.
CREATE CONSTRAINT graphbridge_movies_person_id
FOR (person:GraphBridgeMoviesPerson) REQUIRE person.personId IS UNIQUE;

CREATE CONSTRAINT graphbridge_movies_movie_id
FOR (movie:GraphBridgeMoviesMovie) REQUIRE movie.movieId IS UNIQUE;

CREATE CONSTRAINT graphbridge_movies_acting_credit_id
FOR ()-[credit:GRAPHBRIDGE_MOVIES_ACTED_IN]-() REQUIRE credit.actingCreditId IS UNIQUE;

CREATE CONSTRAINT graphbridge_movies_directing_credit_id
FOR ()-[credit:GRAPHBRIDGE_MOVIES_DIRECTED]-() REQUIRE credit.directingCreditId IS UNIQUE;
