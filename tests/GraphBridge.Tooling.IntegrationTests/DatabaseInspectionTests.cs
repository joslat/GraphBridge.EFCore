using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Scaffolding;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class DatabaseInspectionTests(DisposableNeo4jFixture fixture)
{
    private const string Person = "GraphBridgeScaffoldPerson";
    private const string Movie = "GraphBridgeScaffoldMovie";
    private const string Edge = "GRAPHBRIDGE_SCAFFOLD_ACTED_IN";
    private static GraphInspectionOptions Options(int limit = 32) => new(sampleLimit: limit, labels: [Person, Movie], relationshipTypes: [Edge]);

    [Neo4jIntegrationFact]
    public async Task Live_type_only_inspection_preserves_graph_and_schema_and_reports_parallel_roles_payload()
    {
        await Seed();
        try
        {
            var before = await State();
            var snapshot = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database, Options());
            Assert.Equal(fixture.ServerVersion, snapshot.Capabilities.ServerVersion);
            Assert.Null(snapshot.Schema.GraphTypeSpecification); // Community fallback actually executes.
            Assert.Equal(2, snapshot.Nodes.Count);
            Assert.Equal(2, snapshot.Relationships.Count);
            Assert.All(snapshot.Relationships, edge =>
            {
                Assert.True(edge.ObservedParallel);
                Assert.Equal(GraphScaffoldValueKind.StringList, edge.Properties["roles"]);
                Assert.Contains(Person, edge.SourceLabels);
                Assert.Contains(Movie, edge.TargetLabels);
            });
            Assert.Contains(snapshot.Nodes, node => node.Labels.Count == 2);
            var json = GraphScaffoldJson.WriteSnapshot(snapshot);
            Assert.DoesNotContain("value-must-not-leave-inspection", json);
            Assert.DoesNotContain("nodeToken", json);
            Assert.DoesNotContain("relationshipToken", json);
            Assert.Equal(json, GraphScaffoldJson.WriteSnapshot(GraphScaffoldJson.ReadSnapshot(json)));
            var compilation = GraphScaffoldCompiler.Compile(snapshot, new("Inspected.Movies", "MoviesContext"));
            Assert.True(compilation.Success);
            Assert.Null(compilation.Model);
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "ReadOnlyNoReviewedKey");
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "UnknownCardinality");
            Assert.Equal(before, await State());
        }
        finally { await Cleanup(); }
    }

    [Neo4jIntegrationFact]
    public async Task Reviewed_constraint_backed_policy_compiles_actual_EF_manifest_and_preserves_roles_as_DTO_payload()
    {
        await Seed();
        try
        {
            await Execute($"CREATE CONSTRAINT gb_scaffold_person_key FOR (n:{Person}) REQUIRE n.id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT gb_scaffold_movie_key FOR (n:{Movie}) REQUIRE n.id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT gb_scaffold_edge_key FOR ()-[r:{Edge}]-() REQUIRE r.id IS UNIQUE");
            var before = await State();
            var snapshot = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database, Options());
            var policy = Policy();
            var first = GraphScaffoldCompiler.Compile(snapshot, policy);
            Assert.True(first.Success, string.Join("; ", first.Diagnostics.Select(d => d.Message)));
            Assert.NotNull(first.Model);
            Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "ExplicitlyIgnoredPayload" && diagnostic.Subject.EndsWith(".roles", StringComparison.Ordinal));
            Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "KeyExistenceUnproven");
            var generated = GraphScaffoldArtifacts.Generate(first, snapshot.Capabilities);
            Assert.True(generated.Success, string.Join("; ", generated.Diagnostics.Select(d => d.Message)));
            Assert.NotNull(generated.Fingerprint);
            Assert.Contains(generated.Files, file => file.RelativePath == "graphbridge.binding.json");
            Assert.Contains(generated.Files, file => file.Content.Contains("ScaffoldRelationshipDto", StringComparison.Ordinal));
            var repeated = GraphScaffoldArtifacts.Generate(GraphScaffoldCompiler.Compile(GraphScaffoldJson.ReadSnapshot(GraphScaffoldJson.WriteSnapshot(snapshot)), policy), snapshot.Capabilities);
            Assert.Equal(generated.Files, repeated.Files);
            Assert.Equal(before, await State());
        }
        finally { await Cleanup(); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_constraint_and_conflicting_live_property_types_prevent_tracked_scaffold()
    {
        await Seed();
        try
        {
            await Execute($"MATCH ()-[r:{Edge} {{id:'edge-2'}}]->() SET r.rank = 'conflicting-type'");
            var before = await State();
            var snapshot = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database, Options());
            var compilation = GraphScaffoldCompiler.Compile(snapshot, Policy());
            Assert.False(compilation.Success);
            Assert.Null(compilation.Model);
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "MissingUniqueConstraint");
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "MixedPropertyTypes");
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "PropertyConversionRequired");
            Assert.Equal(before, await State());
        }
        finally { await Cleanup(); }
    }

    [Neo4jIntegrationFact]
    public async Task Live_sampling_bound_cancellation_and_selected_database_errors_leave_borrowed_driver_usable()
    {
        await Seed();
        try
        {
            var sampled = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database, Options(1));
            Assert.Single(sampled.Relationships);
            Assert.False(sampled.Relationships[0].ObservedParallel); // Absence in this sample is not a uniqueness claim.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database, Options(), new(true)));
            await Assert.ThrowsAnyAsync<Neo4jException>(() => GraphDatabaseInspector.InspectAsync(fixture.Driver, "graphbridge_inspection_missing_database", Options()));
            Assert.Equal(2, (await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database, Options())).Relationships.Count);
        }
        finally { await Cleanup(); }
    }

    private static GraphScaffoldPolicy Policy() => new("Inspected.Movies", "MoviesContext",
        [new(Person, "Person", ["id"], true), new(Movie, "Movie", ["id"], true)],
        [new(Edge, "ActedIn", Person, Movie, "id", true,
            [new("roles", "Roles", GraphScalarType.String, Ignore: true)])]);

    private async Task Seed()
    {
        await Cleanup();
        await Execute($"CREATE (p:{Person}:GraphBridgeScaffoldActor {{id:'person-1', name:'value-must-not-leave-inspection'}}), "
            + $"(m:{Movie} {{id:'movie-1', title:'sample', released:1999}}), "
            + $"(p)-[:{Edge} {{id:'edge-1', roles:['Lead'], rank:1}}]->(m), "
            + $"(p)-[:{Edge} {{id:'edge-2', roles:['Cameo'], rank:2}}]->(m)");
    }
    private async Task Cleanup()
    {
        await Execute($"MATCH (:{Person})-[r:{Edge}]->(:{Movie}) DELETE r");
        await Execute($"MATCH (n:{Person}) DELETE n");
        await Execute($"MATCH (n:{Movie}) DELETE n");
        foreach (var name in new[] { "gb_scaffold_person_key", "gb_scaffold_movie_key", "gb_scaffold_edge_key" })
            await Execute($"DROP CONSTRAINT `{name}` IF EXISTS");
    }
    private async Task<string[]> State()
    {
        await using var session = fixture.OpenSession();
        var nodeCursor = await session.RunAsync($"MATCH (n) WHERE n:{Person} OR n:{Movie} RETURN properties(n) AS value ORDER BY n.id");
        var nodes = await nodeCursor.ToListAsync(record => System.Text.Json.JsonSerializer.Serialize(record["value"].As<Dictionary<string, object>>()));
        var edgeCursor = await session.RunAsync($"MATCH (:{Person})-[r:{Edge}]->(:{Movie}) RETURN properties(r) AS value ORDER BY r.id");
        var edges = await edgeCursor.ToListAsync(record => System.Text.Json.JsonSerializer.Serialize(record["value"].As<Dictionary<string, object>>()));
        var schemaCursor = await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name");
        return [.. nodes, .. edges, .. await schemaCursor.ToListAsync(record => record["name"].As<string>())];
    }
    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ToolingIntegrationCollection : ICollectionFixture<DisposableNeo4jFixture>
{
    public const string Name = "GraphBridge tooling disposable Neo4j";
}
