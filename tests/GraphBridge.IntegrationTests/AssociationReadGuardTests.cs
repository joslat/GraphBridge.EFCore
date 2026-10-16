using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>
/// Direct-driver acceptance of the generated read diagnostic and production row decoder.
/// Uses finalized EF metadata without executing an EF association query or materializer.
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class AssociationReadGuardTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string SourceLabel = "GraphBridgeAssociationReadSource";
    private const string TargetLabel = "GraphBridgeAssociationReadTarget";
    private const string SelfLabel = "GraphBridgeAssociationReadSelf";
    private const string EdgeType = "GRAPHBRIDGE_ASSOCIATION_READ";
    private const string OtherType = "GRAPHBRIDGE_ASSOCIATION_READ_OTHER";
    private const string SelfType = "GRAPHBRIDGE_ASSOCIATION_READ_SELF";
    private const string OtherSelfType = "GRAPHBRIDGE_ASSOCIATION_READ_SELF_OTHER";

    [Neo4jIntegrationFact]
    public async Task Valid_full_row_decodes_composite_endpoint_keys_and_nullable_payload_from_driver_records()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, "lead", 7);
            await using var context = Context();
            var read = Binding<Edge>(context);
            Assert.Equal(2, read.RequiredUniqueEndpoints.Count);
            var row = Assert.Single(await ReadAsync(read, scope));
            Assert.Equal(1L, row[CypherQueryPlan.AssociationCountAlias]);

            var values = read.ReadRow(row);
            Assert.Equal(scope.RunId, values[Slot(read, nameof(Edge.SourceRunId)).Index]);
            Assert.Equal(scope.SourceId, values[Slot(read, nameof(Edge.SourceId)).Index]);
            Assert.Equal(scope.RunId, values[Slot(read, nameof(Edge.TargetRunId)).Index]);
            Assert.Equal(scope.TargetId, values[Slot(read, nameof(Edge.TargetId)).Index]);
            Assert.Equal("lead", values[Slot(read, nameof(Edge.Role)).Index]);
            Assert.Equal(7, values[Slot(read, nameof(Edge.Rank)).Index]);
            Assert.Null(values[Slot(read, "Note").Index]);
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Contains(CypherQueryPlan.AssociationCountAlias, context.Set<Edge>().ToQueryString());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Valid_scalar_row_carries_hidden_endpoint_keys_and_decodes_without_other_payload_columns()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, "scalar", 8);
            await using var context = Context();
            var read = Binding<Edge>(context);
            GraphReadProperty[] projection = [Slot(read, nameof(Edge.Role))];
            Assert.Equal(5, read.CreatePlan(projection).Columns.Count);
            var row = Assert.Single(await ReadAsync(read, scope, projection));

            Assert.Equal(6, row.Count); // Four hidden keys, one scalar, one physical-count diagnostic.
            Assert.DoesNotContain(Slot(read, nameof(Edge.Rank)).Column.Alias, row.Keys);
            Assert.DoesNotContain(Slot(read, "Note").Column.Alias, row.Keys);
            Assert.Equal(1L, row[CypherQueryPlan.AssociationCountAlias]);
            var values = read.ReadRow(row, projection);
            Assert.Equal("scalar", values[projection[0].Index]);
            Assert.Equal(scope.SourceId, values[Slot(read, nameof(Edge.SourceId)).Index]);
            Assert.Equal(scope.TargetId, values[Slot(read, nameof(Edge.TargetId)).Index]);
            Assert.Null(values[Slot(read, nameof(Edge.Rank)).Index]);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Parallel_edges_with_only_one_matching_payload_still_report_two_before_filter_and_limit()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, "selected", 1);
            await SeedEdgeAsync(scope, "excluded", 2);
            await using var context = Context();
            var read = Binding<Edge>(context);
            var row = Assert.Single(await ReadAsync(read, scope, selectedRole: "selected", limitOne: true));

            Assert.Equal("selected", row[Slot(read, nameof(Edge.Role)).Column.Alias]);
            Assert.Equal(2L, row[CypherQueryPlan.AssociationCountAlias]);
            Assert.Contains("cardinality diagnostic", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Identical_scalar_values_with_only_one_payload_match_and_limit_do_not_hide_parallel_edges()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, "same", 1);
            await SeedEdgeAsync(scope, "same", 2);
            await using var context = Context();
            var read = Binding<Edge>(context);
            GraphReadProperty[] projection = [Slot(read, nameof(Edge.Role))];
            var row = Assert.Single(await ReadAsync(read, scope, projection, limitOne: true,
                extraPredicate: Equal(Slot(read, nameof(Edge.Rank)), "selectedRank"),
                extraParameters: new Dictionary<string, object> { ["selectedRank"] = 1L }));

            Assert.Equal("same", row[projection[0].Column.Alias]);
            Assert.Equal(2L, row[CypherQueryPlan.AssociationCountAlias]);
            Assert.Contains("cardinality diagnostic", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, projection)).Message);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Scalar_missing_endpoint_key_rejects_even_when_composite_uniqueness_constraint_exists()
    {
        var scope = Scope.New();
        try
        {
            // Neo4j uniqueness is not a property-existence guarantee: source.id deliberately absent.
            await SeedEndpointsAsync(scope, missingSourceId: true);
            await SeedEdgeAsync(scope, "broken key", 1);
            await using var context = Context();
            var read = Binding<Edge>(context);
            GraphReadProperty[] projection = [Slot(read, nameof(Edge.Role))];
            var row = Assert.Single(await ReadAsync(read, scope, projection));

            Assert.Equal(1L, row[CypherQueryPlan.AssociationCountAlias]);
            Assert.Null(row[Slot(read, nameof(Edge.SourceId)).Column.Alias]);
            var error = Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, projection));
            Assert.Contains("required property", error.Message);
            Assert.Contains(nameof(Edge.SourceId), error.Message);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Count_excludes_opposite_direction_and_other_relationship_type_for_same_endpoints()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, "selected", 1);
            await SeedEdgeAsync(scope, "reverse", 2, reverse: true);
            await SeedEdgeAsync(scope, "other type", 3, otherType: true);
            await using var context = Context();
            var read = Binding<Edge>(context);
            var row = Assert.Single(await ReadAsync(read, scope));

            Assert.Equal(1L, row[CypherQueryPlan.AssociationCountAlias]);
            Assert.Equal("selected", read.ReadRow(row)[Slot(read, nameof(Edge.Role)).Index]);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Self_loop_count_is_only_the_exact_bound_source_type_target_tuple()
    {
        var scope = Scope.New();
        try
        {
            await EnsureSchemaAsync();
            await ExecuteAsync(
                "CREATE (s:GraphBridgeAssociationReadSelf {runId:$runId,id:$sourceId}), " +
                "(t:GraphBridgeAssociationReadSelf {runId:$runId,id:$targetId}) " +
                "CREATE (s)-[:GRAPHBRIDGE_ASSOCIATION_READ_SELF {role:'loop'}]->(s), " +
                "(s)-[:GRAPHBRIDGE_ASSOCIATION_READ_SELF {role:'outgoing'}]->(t), " +
                "(t)-[:GRAPHBRIDGE_ASSOCIATION_READ_SELF {role:'incoming'}]->(s), " +
                "(s)-[:GRAPHBRIDGE_ASSOCIATION_READ_SELF_OTHER {role:'other type'}]->(s)", Parameters(scope));
            await using var context = Context();
            var read = Binding<SelfEdge>(context);
            Assert.Single(read.RequiredUniqueEndpoints);
            var predicate = new CypherBinary(
                Equal(Slot(read, nameof(SelfEdge.SourceId)), "selfId"), CypherOperator.And,
                Equal(Slot(read, nameof(SelfEdge.TargetId)), "selfId"));
            var row = Assert.Single(await ReadAsync(read, scope, extraPredicate: predicate,
                extraParameters: new Dictionary<string, object> { ["selfId"] = scope.SourceId.ToString("D") }));

            Assert.Equal(1L, row[CypherQueryPlan.AssociationCountAlias]);
            var values = read.ReadRow(row);
            Assert.Equal("loop", values[Slot(read, nameof(SelfEdge.Role)).Index]);
            Assert.Equal(scope.SourceId, values[Slot(read, nameof(SelfEdge.SourceId)).Index]);
            Assert.Equal(scope.SourceId, values[Slot(read, nameof(SelfEdge.TargetId)).Index]);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task No_matching_edge_produces_no_fabricated_diagnostic_row_for_full_or_scalar_reads()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, "wrong direction", 1, reverse: true);
            await SeedEdgeAsync(scope, "wrong type", 2, otherType: true);
            await using var context = Context();
            var read = Binding<Edge>(context);

            Assert.Empty(await ReadAsync(read, scope));
            Assert.Empty(await ReadAsync(read, scope, [Slot(read, nameof(Edge.Role))]));
        }
        finally { await CleanupAsync(scope); }
    }

    private ReadContext Context() => new(new DbContextOptionsBuilder<ReadContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);

    private static GraphReadBinding Binding<T>(ReadContext context)
        => GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>()
            .GetAssociation(context.Model.FindEntityType(typeof(T))!));
    private static GraphReadProperty Slot(GraphReadBinding read, string name)
        => read.Properties.Single(p => p.ModelProperty.Property.Name == name);
    private static CypherExpression Equal(GraphReadProperty slot, string parameter)
        => new CypherBinary(slot.StoreProperty, CypherOperator.Equal, new CypherParameter(parameter));

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(GraphReadBinding read, Scope scope,
        IReadOnlyList<GraphReadProperty>? projection = null, string? selectedRole = null, bool limitOne = false,
        CypherExpression? extraPredicate = null, IDictionary<string, object>? extraParameters = null)
    {
        await using var session = fixture.OpenSession();
        // Observe actual server schema, then invoke the same production validation used for node writes.
        var schemaCursor = await session.RunAsync(
            "SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties");
        var constraints = await schemaCursor.ToListAsync(record => new GraphSchemaConstraint(
            record["entityType"].As<string>(), record["type"].As<string>(),
            record["labelsOrTypes"].As<List<string>>(), record["properties"].As<List<string>>()));
        GraphKeyConstraintValidator.Validate(read.RequiredUniqueEndpoints, constraints);

        CypherExpression predicate = new CypherBinary(Equal(Slot(read, "SourceRunId"), "runId"), CypherOperator.And,
            Equal(Slot(read, "TargetRunId"), "runId"));
        var parameters = new Dictionary<string, object> { ["runId"] = scope.RunId };
        if (selectedRole is not null)
        {
            predicate = new CypherBinary(predicate, CypherOperator.And, Equal(Slot(read, "Role"), "role"));
            parameters["role"] = selectedRole;
        }
        if (extraPredicate is not null)
            predicate = new CypherBinary(predicate, CypherOperator.And, extraPredicate);
        if (extraParameters is not null)
            foreach (var (key, value) in extraParameters) parameters.Add(key, value);
        if (limitOne) parameters["take"] = 1L;
        var plan = read.CreatePlan(projection) with { Predicate = predicate, LimitParameter = limitOne ? "take" : null };
        var text = CypherQueryRenderer.Render(plan);
        Assert.DoesNotContain(scope.RunId, text);
        output.WriteLine($"Association read contract on Neo4j {fixture.ServerVersion} {fixture.ServerEdition}; no EF query activation.");
        output.WriteLine(text);
        var cursor = await session.RunAsync(text, parameters);
        return await cursor.ToListAsync(Row);
    }

    private static IReadOnlyDictionary<string, object?> Row(IRecord record)
        => record.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

    private async Task EnsureSchemaAsync()
    {
        // Reviewed fixed fixture-only DDL. Runtime provider operations never create schema.
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_association_read_source_keys IF NOT EXISTS FOR (n:GraphBridgeAssociationReadSource) REQUIRE (n.runId,n.id) IS UNIQUE", new Dictionary<string, object>());
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_association_read_target_keys IF NOT EXISTS FOR (n:GraphBridgeAssociationReadTarget) REQUIRE (n.runId,n.id) IS UNIQUE", new Dictionary<string, object>());
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_association_read_self_keys IF NOT EXISTS FOR (n:GraphBridgeAssociationReadSelf) REQUIRE (n.runId,n.id) IS UNIQUE", new Dictionary<string, object>());
    }

    private async Task SeedEndpointsAsync(Scope scope, bool missingSourceId = false)
    {
        await EnsureSchemaAsync();
        var parameters = Parameters(scope);
        parameters["sourceId"] = missingSourceId ? null! : scope.SourceId.ToString("D");
        await ExecuteAsync("CREATE (:GraphBridgeAssociationReadSource {runId:$runId,id:$sourceId}), " +
            "(:GraphBridgeAssociationReadTarget {runId:$runId,id:$targetId})", parameters);
    }

    private Task SeedEdgeAsync(Scope scope, string role, int rank, bool reverse = false, bool otherType = false)
    {
        var type = otherType ? OtherType : EdgeType; // Only fixed fixture metadata, never caller input.
        var parameters = Parameters(scope);
        parameters["role"] = role;
        parameters["rank"] = (long)rank;
        // Source lookup deliberately allows missing id so the row decoder's key guard is exercised.
        return ExecuteAsync("MATCH (s:GraphBridgeAssociationReadSource {runId:$runId}), " +
            "(t:GraphBridgeAssociationReadTarget {runId:$runId,id:$targetId}) " +
            (reverse ? $"CREATE (t)-[r:{type}]->(s) " : $"CREATE (s)-[r:{type}]->(t) ") +
            "SET r.role = $role, r.rank = $rank", parameters);
    }

    private async Task ExecuteAsync(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task CleanupAsync(Scope scope)
    {
        // Restrict every edge by fixed type and both run-scoped endpoint labels; never DETACH.
        foreach (var type in new[] { EdgeType, OtherType })
            await ExecuteAsync($"MATCH (s:{SourceLabel} {{runId:$runId}})-[r:{type}]-(t:{TargetLabel} {{runId:$runId}}) DELETE r", Parameters(scope));
        foreach (var type in new[] { SelfType, OtherSelfType })
            await ExecuteAsync($"MATCH (s:{SelfLabel} {{runId:$runId}})-[r:{type}]->(t:{SelfLabel} {{runId:$runId}}) DELETE r", Parameters(scope));
        foreach (var label in new[] { SourceLabel, TargetLabel, SelfLabel })
            await ExecuteAsync($"MATCH (n:{label} {{runId:$runId}}) DELETE n", Parameters(scope));
    }

    private static Dictionary<string, object> Parameters(Scope scope) => new()
    {
        ["runId"] = scope.RunId, ["sourceId"] = scope.SourceId.ToString("D"), ["targetId"] = scope.TargetId.ToString("D")
    };
    private sealed record Scope(string RunId, Guid SourceId, Guid TargetId)
    {
        public static Scope New() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid());
    }

    public sealed class ReadContext(DbContextOptions<ReadContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            Endpoint(model.Entity<Source>(), SourceLabel);
            Endpoint(model.Entity<Target>(), TargetLabel);
            Endpoint(model.Entity<SelfNode>(), SelfLabel);
            var edge = model.Entity<Edge>();
            edge.HasKey(e => new { e.SourceRunId, e.SourceId, e.TargetRunId, e.TargetId });
            edge.HasOne<Source>().WithMany().HasForeignKey(e => new { e.SourceRunId, e.SourceId });
            edge.HasOne<Target>().WithMany().HasForeignKey(e => new { e.TargetRunId, e.TargetId });
            edge.Property(e => e.Role).HasGraphPropertyName("role");
            edge.Property(e => e.Rank).HasGraphPropertyName("rank");
            edge.Property<string?>("Note").HasGraphPropertyName("note");
            edge.ToGraphRelationship(EdgeType, [nameof(Edge.SourceRunId), nameof(Edge.SourceId)],
                [nameof(Edge.TargetRunId), nameof(Edge.TargetId)]);
            var self = model.Entity<SelfEdge>();
            self.HasKey(e => new { e.SourceRunId, e.SourceId, e.TargetRunId, e.TargetId });
            self.HasOne<SelfNode>().WithMany().HasForeignKey(e => new { e.SourceRunId, e.SourceId });
            self.HasOne<SelfNode>().WithMany().HasForeignKey(e => new { e.TargetRunId, e.TargetId });
            self.Property(e => e.Role).HasGraphPropertyName("role");
            self.ToGraphRelationship(SelfType, [nameof(SelfEdge.SourceRunId), nameof(SelfEdge.SourceId)],
                [nameof(SelfEdge.TargetRunId), nameof(SelfEdge.TargetId)]);
        }
        private static void Endpoint<T>(EntityTypeBuilder<T> entity, string label) where T : class
        {
            entity.ToGraphNode(label);
            entity.HasKey("RunId", "Id");
            entity.Property<string>("RunId").HasGraphPropertyName("runId");
            entity.Property<Guid>("Id").HasGraphPropertyName("id").ValueGeneratedNever();
        }
    }

    public sealed class Source { public string RunId { get; set; } = ""; public Guid Id { get; set; } }
    public sealed class Target { public string RunId { get; set; } = ""; public Guid Id { get; set; } }
    public sealed class SelfNode { public string RunId { get; set; } = ""; public Guid Id { get; set; } }
    public sealed class Edge
    {
        public string SourceRunId { get; set; } = "";
        public Guid SourceId { get; set; }
        public string TargetRunId { get; set; } = "";
        public Guid TargetId { get; set; }
        public string? Role { get; set; }
        public int? Rank { get; set; }
    }
    public sealed class SelfEdge
    {
        public string SourceRunId { get; set; } = "";
        public Guid SourceId { get; set; }
        public string TargetRunId { get; set; } = "";
        public Guid TargetId { get; set; }
        public string? Role { get; set; }
    }
}
