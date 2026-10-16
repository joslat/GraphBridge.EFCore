# GraphBridge.EntityFrameworkCore

Experimental non-relational EF Core provider for Neo4j. EF owns the finalized model, tracking and materialization; Neo4j.Driver owns transport.

The tested baseline is .NET 10, EF Core 10.0.12 and Neo4j.Driver 6.3.0 against Neo4j 2026.09.0 Community. Pin these tested patches in consuming applications. A separate Neo4j 2026.09.0 Enterprise profile verifies dedicated-database reader/writer permissions and denial paths. Broader version compatibility, Enterprise schema deployment and clustering are unverified.

Configure an externally owned `IDriver` with `UseNeo4j(driver, databaseName)`, then map application-keyed entities using `ToGraphNode`, `ToGraphReference` or explicit relationship mappings. Deploy reviewed key constraints before saving. Queries and saves never create schema implicitly. Unsupported query/model shapes throw; this package does not promise the complete EF relational surface.

See the [repository documentation](https://github.com/joslat/GraphBridge.EFCore), [published verification](https://github.com/joslat/GraphBridge.EFCore/blob/main/tests/verification/VERIFICATION_SUMMARY.md) and [three samples](https://github.com/joslat/GraphBridge.EFCore/tree/main/samples) for executable workflows and limitations. Optional authoring tools and generators are separate from runtime dependencies.

Actual provider package consumers pass locally and in hosted CI. This is the experimental `0.1.0-preview.2` developer preview; see the [changelog](https://github.com/joslat/GraphBridge.EFCore/blob/main/CHANGELOG.md).
