// Generated proposal for declared 2026.09.0 community.
// Review before explicit deployment. No live capabilities or graph data were observed.
CREATE CONSTRAINT `gb_ct_0c431df43cac5f3b1ad89b62` FOR ()-[r:`GRAPHBRIDGE_MOVIES_ACTED_IN`]-() REQUIRE (r.`actingCreditId`) IS UNIQUE;
CREATE CONSTRAINT `gb_ct_0ebc619183ee5e2419a2b78e` FOR (n:`GraphBridgeMoviesMovie`) REQUIRE (n.`movieId`) IS UNIQUE;
CREATE CONSTRAINT `gb_ct_6496247e985638a35b79874c` FOR ()-[r:`GRAPHBRIDGE_MOVIES_DIRECTED`]-() REQUIRE (r.`directingCreditId`) IS UNIQUE;
CREATE CONSTRAINT `gb_ct_68cce8800595491ccc07f8f8` FOR (n:`GraphBridgeMoviesPerson`) REQUIRE (n.`personId`) IS UNIQUE;
