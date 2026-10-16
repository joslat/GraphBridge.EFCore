# Database-first inference proposal

Source observation/policy SHA-256: `a9b1d0e1f32f7d4978063c9319f1f7abcf701c7b75b3112d31cd23613b5f73b9` (not a runtime binding fingerprint).

Server: "2026.09.0"; edition: "community"; sample limit per selected label/type: 32.

No writes were performed by this pure compiler. The finalized generated EF IModel must be compiled separately to obtain the actual binding manifest. No timestamp, connection address, credentials, raw property values or element IDs are included.

## Schema evidence and reviewed decisions


## Observed inventory and sample distributions

- Node "GraphBridgeMoviesMovie": 2 sampled observations; read-only DTO.
  - "movieId": String=2; absent=0.
  - "released": Integer=2; absent=0.
  - "tagline": String=1; absent=1.
  - "title": String=2; absent=0.
- Node "GraphBridgeMoviesPerson": 3 sampled observations; read-only DTO.
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

- Warning ObservedParallelEdges ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Parallel relationships were observed; an independently identified edge is required for tracking.
- Warning ReadOnlyRelationship ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Relationship payload and endpoints remain read-only DTO data until an explicit identified-edge mapping is reviewed.
- Warning UnknownCardinality ["GRAPHBRIDGE_MOVIES_ACTED_IN"]: Bounded relationship samples cannot establish one-to-one, one-to-many or association uniqueness. No collection navigation or association-edge identity is inferred.
- Warning OpaquePayload ["GRAPHBRIDGE_MOVIES_ACTED_IN.roles"]: Payload requires an explicit mapping decision. Raw DTO property bags preserve values; lists, temporal and spatial values are not supported by automatic tracked scalar inference.
- Warning ReadOnlyRelationship ["GRAPHBRIDGE_MOVIES_DIRECTED"]: Relationship payload and endpoints remain read-only DTO data until an explicit identified-edge mapping is reviewed.
- Warning UnknownCardinality ["GRAPHBRIDGE_MOVIES_DIRECTED"]: Bounded relationship samples cannot establish one-to-one, one-to-many or association uniqueness. No collection navigation or association-edge identity is inferred.
- Warning ReadOnlyNoReviewedKey ["GraphBridgeMoviesMovie"]: No reviewed constraint-backed application identity. Preserve this label as read-only DTO data; names, titles, sampled uniqueness and elementId() are never chosen as keys.
- Warning ReadOnlyNoReviewedKey ["GraphBridgeMoviesPerson"]: No reviewed constraint-backed application identity. Preserve this label as read-only DTO data; names, titles, sampled uniqueness and elementId() are never chosen as keys.
- Warning ReviewRequired ["policy"]: Generated configuration is a reviewable proposal. No graph writes, identity backfill, migration or schema deployment is performed by compilation.
- Warning GraphTypeNotObserved ["schema"]: Graph types were unavailable or not requested; constraints, indexes and bounded samples remain usable without Graph Types.
- Warning BoundedObservation ["snapshot"]: Samples are bounded and read-committed observations, not a consistent full graph snapshot. Types, requiredness, endpoint coverage and cardinality remain uncertain even below the sample limit.
