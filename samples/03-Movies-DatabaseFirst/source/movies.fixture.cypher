// Original GraphBridge fixture; parameters are in movies.fixture.parameters.json.
// Review and execute only against this repository's owned disposable fixture.
// This statement creates data only when all four sample labels/types are empty.
// A caller must require exactly one result row with counts 3, 2, 4, 1 before commit.
// Zero rows means existing sample data was found; do not retry or delete it automatically.
CALL {
    OPTIONAL MATCH (person:GraphBridgeMoviesPerson)
    WITH person LIMIT 1
    RETURN count(person) AS peopleBefore
}
CALL {
    OPTIONAL MATCH (movie:GraphBridgeMoviesMovie)
    WITH movie LIMIT 1
    RETURN count(movie) AS moviesBefore
}
CALL {
    OPTIONAL MATCH ()-[credit:GRAPHBRIDGE_MOVIES_ACTED_IN|GRAPHBRIDGE_MOVIES_DIRECTED]->()
    WITH credit LIMIT 1
    RETURN count(credit) AS creditsBefore
}
WITH peopleBefore, moviesBefore, creditsBefore
WHERE peopleBefore = 0 AND moviesBefore = 0 AND creditsBefore = 0
CALL {
    UNWIND $people AS item
    CREATE (person:GraphBridgeMoviesPerson {
        personId: item.personId, name: item.name, born: item.born
    })
    RETURN count(person) AS peopleCreated
}
CALL {
    UNWIND $movies AS item
    CREATE (movie:GraphBridgeMoviesMovie {
        movieId: item.movieId, title: item.title, released: item.released, tagline: item.tagline
    })
    RETURN count(movie) AS moviesCreated
}
CALL {
    UNWIND $actingCredits AS item
    MATCH (person:GraphBridgeMoviesPerson {personId: item.personId})
    MATCH (movie:GraphBridgeMoviesMovie {movieId: item.movieId})
    CREATE (person)-[credit:GRAPHBRIDGE_MOVIES_ACTED_IN {
        actingCreditId: item.actingCreditId, roles: item.roles, billingOrder: item.billingOrder
    }]->(movie)
    RETURN count(credit) AS actingCreditsCreated
}
CALL {
    UNWIND $directingCredits AS item
    MATCH (person:GraphBridgeMoviesPerson {personId: item.personId})
    MATCH (movie:GraphBridgeMoviesMovie {movieId: item.movieId})
    CREATE (person)-[credit:GRAPHBRIDGE_MOVIES_DIRECTED {
        directingCreditId: item.directingCreditId, creditNote: item.creditNote
    }]->(movie)
    RETURN count(credit) AS directingCreditsCreated
}
RETURN peopleCreated, moviesCreated, actingCreditsCreated, directingCreditsCreated;
