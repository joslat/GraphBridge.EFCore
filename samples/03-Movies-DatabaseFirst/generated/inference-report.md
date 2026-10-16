# Database-first inference proposal

Source observation/policy SHA-256: `6c23bbaf4b02fb5e0c6af37e2121bc1f72ac1c3147332c844c2046fa28a83790` (not a runtime binding fingerprint).

Server: "2026.09.0"; edition: "community"; sample limit per selected label/type: 32.

No writes were performed by this pure compiler. The finalized generated EF IModel must be compiled separately to obtain the actual binding manifest. No timestamp, connection address, credentials, raw property values or element IDs are included.

## Schema evidence and reviewed decisions

- Constraint "graphbridge_movies_acting_credit_id": "RELATIONSHIP" "RELATIONSHIP_PROPERTY_UNIQUENESS" on ["GRAPHBRIDGE_MOVIES_ACTED_IN"] properties ["actingCreditId"].
- Constraint "graphbridge_movies_directing_credit_id": "RELATIONSHIP" "RELATIONSHIP_PROPERTY_UNIQUENESS" on ["GRAPHBRIDGE_MOVIES_DIRECTED"] properties ["directingCreditId"].
- Constraint "graphbridge_movies_movie_id": "NODE" "NODE_PROPERTY_UNIQUENESS" on ["GraphBridgeMoviesMovie"] properties ["movieId"].
- Constraint "graphbridge_movies_person_id": "NODE" "NODE_PROPERTY_UNIQUENESS" on ["GraphBridgeMoviesPerson"] properties ["personId"].
- Index "graphbridge_movies_acting_credit_id": "RANGE" state "ONLINE" on ["GRAPHBRIDGE_MOVIES_ACTED_IN"] properties ["actingCreditId"]; indexes alone are not identity constraints.
- Index "graphbridge_movies_directing_credit_id": "RANGE" state "ONLINE" on ["GRAPHBRIDGE_MOVIES_DIRECTED"] properties ["directingCreditId"]; indexes alone are not identity constraints.
- Index "graphbridge_movies_movie_id": "RANGE" state "ONLINE" on ["GraphBridgeMoviesMovie"] properties ["movieId"]; indexes alone are not identity constraints.
- Index "graphbridge_movies_person_id": "RANGE" state "ONLINE" on ["GraphBridgeMoviesPerson"] properties ["personId"]; indexes alone are not identity constraints.
- Reviewed node "GraphBridgeMoviesMovie" -> "Movie"; ordered application key ["movieId"]; key/requiredness review acknowledged: true.
- Reviewed node "GraphBridgeMoviesPerson" -> "Person"; ordered application key ["personId"]; key/requiredness review acknowledged: true.
- Reviewed identified edge "GRAPHBRIDGE_MOVIES_ACTED_IN" -> "ActingCredit"; application key "actingCreditId"; source "GraphBridgeMoviesPerson" -> target "GraphBridgeMoviesMovie"; key/requiredness review acknowledged: true.
- Reviewed identified edge "GRAPHBRIDGE_MOVIES_DIRECTED" -> "DirectingCredit"; application key "directingCreditId"; source "GraphBridgeMoviesPerson" -> target "GraphBridgeMoviesMovie"; key/requiredness review acknowledged: true.

## Observed inventory and sample distributions

- Node "GraphBridgeMoviesMovie": 2 sampled observations; reviewed tracking proposal.
  - "movieId": String=2; absent=0.
  - "released": Integer=2; absent=0.
  - "tagline": String=1; absent=1.
  - "title": String=2; absent=0.
- Node "GraphBridgeMoviesPerson": 3 sampled observations; reviewed tracking proposal.
  - "born": Integer=2; absent=1.
  - "name": String=3; absent=0.
  - "personId": String=3; absent=0.
- Directed relationship "GRAPHBRIDGE_MOVIES_ACTED_IN": 4 sampled observations; cardinality unknown.
  - Endpoint labels: ["GraphBridgeMoviesPerson"] -> ["GraphBridgeMoviesMovie"].
  - "actingCreditId": String=4; absent=0.
  - "billingOrder": Integer=4; absent=0.
  - "roles": StringList=4; absent=0.
- Directed relationship "GRAPHBRIDGE_MOVIES_DIRECTED": 1 sampled observations; cardinality unknown.
  - Endpoint labels: ["GraphBridgeMoviesPerson"] -> ["GraphBridgeMoviesMovie"].
  - "creditNote": String=1; absent=0.
  - "directingCreditId": String=1; absent=0.

## Decisions and uncertainty

- Warning IdentifiedEdgeDecision ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Explicit identified-edge mapping selected, preserving payload and parallel identity; no association-edge or collection-navigation inference is performed.
- Warning KeyExistenceUnproven ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Uniqueness does not imply existence. Requiredness is a reviewed application assumption, not proven by bounded samples; unsampled records may fail materialization. Use a separate reviewed backfill/validation plan if needed.
- Warning ObservedParallelEdges ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Parallel relationships were observed; an independently identified edge is required for tracking.
- Warning UnknownCardinality ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Bounded relationship samples cannot establish one-to-one, one-to-many or association uniqueness. No collection navigation or association-edge identity is inferred.
- Warning ReviewedPropertyAssumption ["GRAPHBRIDGE_MOVIES_ACTED_IN.actingCreditId"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GRAPHBRIDGE_MOVIES_ACTED_IN.billingOrder"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ExplicitlyIgnoredPayload ["GRAPHBRIDGE_MOVIES_ACTED_IN.roles"]: Explicitly excluded from tracked EF mapping; raw values remain available in read-only DTO property bags. Existing omitted payload is not converted or rewritten.
- Warning OpaquePayload ["GRAPHBRIDGE_MOVIES_ACTED_IN.roles"]: Payload requires an explicit mapping decision. Raw DTO property bags preserve values; lists, temporal and spatial values are not supported by automatic tracked scalar inference.
- Warning IdentifiedEdgeDecision ["GRAPHBRIDGE_MOVIES_DIRECTED"]: Explicit identified-edge mapping selected, preserving payload and parallel identity; no association-edge or collection-navigation inference is performed.
- Warning KeyExistenceUnproven ["GRAPHBRIDGE_MOVIES_DIRECTED"]: Uniqueness does not imply existence. Requiredness is a reviewed application assumption, not proven by bounded samples; unsampled records may fail materialization. Use a separate reviewed backfill/validation plan if needed.
- Warning UnknownCardinality ["GRAPHBRIDGE_MOVIES_DIRECTED"]: Bounded relationship samples cannot establish one-to-one, one-to-many or association uniqueness. No collection navigation or association-edge identity is inferred.
- Warning ReviewedPropertyAssumption ["GRAPHBRIDGE_MOVIES_DIRECTED.creditNote"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GRAPHBRIDGE_MOVIES_DIRECTED.directingCreditId"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning KeyExistenceUnproven ["GraphBridgeMoviesMovie"]: Uniqueness does not imply existence. Requiredness is a reviewed application assumption, not proven by bounded samples; unsampled records may fail materialization. Use a separate reviewed backfill/validation plan if needed.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesMovie.movieId"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesMovie.released"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesMovie.tagline"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesMovie.title"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning KeyExistenceUnproven ["GraphBridgeMoviesPerson"]: Uniqueness does not imply existence. Requiredness is a reviewed application assumption, not proven by bounded samples; unsampled records may fail materialization. Use a separate reviewed backfill/validation plan if needed.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesPerson.born"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesPerson.name"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewedPropertyAssumption ["GraphBridgeMoviesPerson.personId"]: Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data.
- Warning ReviewRequired ["policy"]: Generated configuration is a reviewable proposal. No graph writes, identity backfill, migration or schema deployment is performed by compilation.
- Warning GraphTypeNotObserved ["schema"]: Graph types were unavailable or not requested; constraints, indexes and bounded samples remain usable without Graph Types.
- Warning BoundedObservation ["snapshot"]: Samples are bounded and read-committed observations, not a consistent full graph snapshot. Types, requiredness, endpoint coverage and cardinality remain uncertain even below the sample limit.
