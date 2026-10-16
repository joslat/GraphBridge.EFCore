using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderIdentifiedEdgeQueryTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string SourceLabel = "GraphBridgeIdentifiedQuerySource";
    private const string TargetLabel = "GraphBridgeIdentifiedQueryTarget";
    private const string EdgeType = "GRAPHBRIDGE_IDENTIFIED_QUERY_EVENT";
    private const string CorruptType = "GRAPHBRIDGE_IDENTIFIED_QUERY_CORRUPT";
    private const string NoSchemaType = "GRAPHBRIDGE_IDENTIFIED_QUERY_NO_SCHEMA";
    private const string OtherLabel = "GraphBridgeIdentifiedQueryOtherEndpoint";

    [Neo4jIntegrationFact]
    public async Task Parallel_same_pair_edges_keep_distinct_EF_identity_and_fix_up_in_fresh_contexts()
    {
        var scope = Scope.New();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, firstId, "first", 1);
            await SeedEdgeAsync(scope, secondId, "second", 2);
            await using var context = Context();
            var trackedFromQuery = 0;
            context.ChangeTracker.Tracked += (_, e) => { Assert.True(e.FromQuery); trackedFromQuery++; };
            var source = await context.Set<Source>().SingleAsync(e => e.Tenant == scope.RunId);
            var target = await context.Set<Target>().SingleAsync(e => e.Tenant == scope.RunId);
            var edges = await context.Set<Edge>().Where(e => e.SourceTenant == scope.RunId).OrderBy(e => e.Rank).ToListAsync();
            Assert.Equal(new[] { firstId, secondId }, edges.Select(e => e.Id));
            Assert.NotSame(edges[0], edges[1]);
            Assert.All(edges, e => { Assert.Same(source, e.Source); Assert.Same(target, e.Target); });
            Assert.Equal(2, source.Edges.Count);
            Assert.Equal(2, target.Edges.Count);
            Assert.Equal(4, trackedFromQuery);
            Assert.False(context.Entry(source).Collection(e => e.Edges).IsLoaded);
            edges[0].Role = "pending";
            context.ChangeTracker.DetectChanges();
            Assert.Same(edges[0], await context.Set<Edge>().SingleAsync(e => e.Id == firstId));
            Assert.Equal("pending", edges[0].Role);
            Assert.Equal("first", context.Entry(edges[0]).Property(e => e.Role).OriginalValue);

            await using var fresh = Context();
            var freshEdges = await fresh.Set<Edge>().Where(e => e.SourceTenant == scope.RunId).OrderBy(e => e.Rank).ToListAsync();
            Assert.Equal(2, freshEdges.Count);
            Assert.NotSame(edges[0], freshEdges[0]);
            Assert.All(freshEdges, e => { Assert.Null(e.Source); Assert.Null(e.Target); });
            var freshSource = await fresh.Set<Source>().SingleAsync(e => e.Tenant == scope.RunId);
            var freshTarget = await fresh.Set<Target>().SingleAsync(e => e.Tenant == scope.RunId);
            Assert.All(freshEdges, e => { Assert.Same(freshSource, e.Source); Assert.Same(freshTarget, e.Target); });
            Assert.Equal(2, freshSource.Edges.Count);
            Assert.Equal("first", freshEdges[0].Role);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Endpoint_and_payload_predicates_scalar_paging_and_converters_preserve_hidden_identity()
    {
        var scope = Scope.New();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, firstId, null, 1);
            await SeedEdgeAsync(scope, secondId, "selected", 2);
            await using var context = Context();
            var minimum = 1;
            var query = context.Set<Edge>().Where(e => e.SourceTenant == scope.RunId && e.SourceId == scope.SourceId && e.Rank >= minimum);
            var page = query.OrderBy(e => e.Role).ThenBy(e => e.Rank).Skip(1).Take(1).Select(e => e.Role);
            var text = page.ToQueryString();
            output.WriteLine(text);
            Assert.Contains("r.`edge_key` AS", text);
            Assert.Contains("s.`source_key` AS", text);
            Assert.Contains("t.`target_key` AS", text);
            Assert.Contains(CypherQueryPlan.IdentifiedEdgeCountAlias, text);
            Assert.DoesNotContain(CypherQueryPlan.AssociationCountAlias, text);
            Assert.DoesNotContain(scope.RunId, text);
            Assert.Equal("selected", Assert.Single(await page.ToListAsync()));
            Assert.Equal(new string?[] { null, "selected" }, await query.OrderBy(e => e.Role).Select(e => e.Role).ToListAsync());
            Assert.Equal(firstId, await query.Where(e => e.Rank == 1).Select(e => e.Id).SingleAsync());
            Assert.Equal(scope.TargetId, await query.Where(e => e.Rank == 1).Select(e => e.TargetId).SingleAsync());
            Assert.Equal(1.23m, await query.Where(e => e.Rank == 1).Select(e => e.Weight).SingleAsync());
            Assert.Null(await query.Where(e => e.Rank == 1).Select(e => EF.Property<string?>(e, "Note")).SingleAsync());
            minimum = 2;
            Assert.Equal(secondId, await query.Select(e => e.Id).SingleAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task No_tracking_modes_do_not_merge_parallel_edges_or_fix_up_context_endpoints()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Guid.NewGuid(), "one", 1);
            await SeedEdgeAsync(scope, Guid.NewGuid(), "two", 2);
            await using var context = Context();
            var source = await context.Set<Source>().SingleAsync(e => e.Tenant == scope.RunId);
            var target = await context.Set<Target>().SingleAsync(e => e.Tenant == scope.RunId);
            foreach (var identityResolution in new[] { false, true })
            {
                var query = context.Set<Edge>().Where(e => e.SourceTenant == scope.RunId).OrderBy(e => e.Rank).AsQueryable();
                query = identityResolution ? query.AsNoTrackingWithIdentityResolution() : query.AsNoTracking();
                var first = await query.ToListAsync();
                var second = await query.ToListAsync();
                Assert.Equal(2, first.Count);
                Assert.NotEqual(first[0].Id, first[1].Id);
                Assert.NotSame(first[0], first[1]);
                Assert.NotSame(first[0], second[0]);
                Assert.All(first, e => { Assert.Null(e.Source); Assert.Null(e.Target); });
                Assert.Empty(context.ChangeTracker.Entries<Edge>());
                Assert.Empty(source.Edges);
                Assert.Empty(target.Edges);
            }
            Assert.Equal(2, context.ChangeTracker.Entries().Count());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_application_ID_rejects_entity_and_scalar_rows_before_tracking_despite_unique_constraint()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, null, "missing identity", 1);
            await using var context = Context();
            var query = context.Set<Edge>().Where(e => e.SourceTenant == scope.RunId);
            Assert.Contains("identity diagnostic", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync())).Message);
            Assert.Contains("identity diagnostic", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(e => e.Role).ToListAsync())).Message);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Scalar_read_validates_hidden_endpoint_keys_and_bad_ID_conversion_before_EF_shaping()
    {
        var scope = Scope.New() with { SourceId = Guid.Parse("abcdef00-1234-5678-90ab-cdef00123456") };
        try
        {
            await SeedEndpointsAsync(scope, missingSourceKey: true);
            await SeedEdgeAsync(scope, Guid.NewGuid(), "bad endpoint", 1);
            await using var context = Context();
            var query = context.Set<Edge>().Where(e => e.SourceTenant == scope.RunId);
            Assert.Contains("required property", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(e => e.Role).ToListAsync())).Message);
            Assert.Empty(context.ChangeTracker.Entries());
            var noncanonicalEndpoint = Parameters(scope);
            noncanonicalEndpoint["sourceId"] = scope.SourceId.ToString("D").ToUpperInvariant();
            await ExecuteAsync($"MATCH (n:{SourceLabel} {{tenant:$runId}}) SET n.source_key=$sourceId", noncanonicalEndpoint);
            Assert.Contains("canonical store representation", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(e => e.Role).ToListAsync())).Message);
            Assert.Contains("canonical store representation", (await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Source>().Where(e => e.Tenant == scope.RunId).ToListAsync())).Message);
            await ExecuteAsync($"MATCH (n:{SourceLabel} {{tenant:$runId}}) SET n.source_key=$sourceId", Parameters(scope));
            await ExecuteAsync($"MATCH (s:{SourceLabel} {{tenant:$runId}})-[r:{EdgeType}]->() SET r.edge_key='ABCDEF00-1234-5678-90AB-CDEF00123456'", Parameters(scope));
            Assert.Contains("canonical store representation", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync())).Message);
            Assert.Contains("canonical store representation", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(e => e.Role).ToListAsync())).Message);
            await ExecuteAsync($"MATCH (s:{SourceLabel} {{tenant:$runId}})-[r:{EdgeType}]->() SET r.edge_key='malformed-guid'", Parameters(scope));
            await Assert.ThrowsAsync<FormatException>(() => query.ToListAsync());
            await Assert.ThrowsAsync<FormatException>(() => query.Select(e => e.Role).ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_relationship_ID_constraint_rejects_empty_query_without_implicit_DDL()
    {
        await EnsureSchemaAsync();
        await using var context = new NoSchemaContext(new DbContextOptionsBuilder<NoSchemaContext>()
            .UseNeo4j(fixture.Driver, fixture.Database).Options);
        var id = Guid.NewGuid();
        var query = context.Set<Edge>().Where(e => e.Id == id).Select(e => e.Role);
        Assert.Contains(CypherQueryPlan.IdentifiedEdgeCountAlias, query.ToQueryString());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
        Assert.Contains("uniqueness constraint", error.Message);
        Assert.Contains(NoSchemaType, error.Message);
        Assert.Empty(context.ChangeTracker.Entries());
        await using var session = fixture.OpenSession();
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD labelsOrTypes RETURN labelsOrTypes")).ToListAsync();
        Assert.DoesNotContain(constraints, r => r["labelsOrTypes"].As<List<string>>().Contains(NoSchemaType));
    }

    [Neo4jIntegrationFact]
    public async Task Generated_guard_finds_duplicate_ID_on_another_unmapped_endpoint_pair_before_payload_filter_and_limit()
    {
        // Deliberately corrupt, unconstrained fixture type: direct-driver execution proves the generated
        // guard. Actual EF execution must reject its absent ID constraint before reading any entities.
        var scope = Scope.New();
        var id = Guid.NewGuid();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, id, "selected", 1, type: CorruptType);
            var parameters = Parameters(scope);
            parameters["edgeId"] = id.ToString("D");
            await ExecuteAsync($"CREATE (a:{OtherLabel} {{tenant:$runId}}), (b:{OtherLabel} {{tenant:$runId}}), (a)-[:{CorruptType} {{edge_key:$edgeId,role:'hidden'}}]->(b)", parameters);
            await using var context = new CorruptContext(new DbContextOptionsBuilder<CorruptContext>()
                .UseNeo4j(fixture.Driver, fixture.Database).Options);
            var binding = GraphReadBinding.FromIdentifiedEdge(context.GetService<GraphBindingCompiler>()
                .GetIdentifiedEdge(context.Model.FindEntityType(typeof(Edge))!));
            var role = binding.Properties.Single(p => p.ModelProperty.Property.Name == nameof(Edge.Role));
            var plan = binding.CreatePlan([role]) with
            {
                Predicate = new CypherBinary(new CypherProperty("role", CypherPropertyOwner.Relationship), CypherOperator.Equal, new CypherParameter("role")),
                LimitParameter = "limit"
            };
            var text = CypherQueryRenderer.Render(plan);
            output.WriteLine(text);
            await using var session = fixture.OpenSession();
            var row = Assert.Single(await (await session.RunAsync(text, new { role = "selected", limit = 1L })).ToListAsync());
            Assert.Equal(2L, row[CypherQueryPlan.IdentifiedEdgeCountAlias].As<long>());
            Assert.Contains("identity diagnostic", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(
                row.Values.ToDictionary(p => p.Key, p => (object?)p.Value), [role])).Message);
            Assert.Contains("uniqueness constraint", (await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Edge>().ToListAsync())).Message);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Identified_queries_reuse_transaction_and_dispose_early_enumeration_and_cancellation()
    {
        var scope = Scope.New();
        var id = Guid.NewGuid();
        try
        {
            await SeedEndpointsAsync(scope);
            await using var context = Context();
            var query = context.Set<Edge>().Where(e => e.SourceTenant == scope.RunId);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(cancellation.Token));
            Assert.Null(context.Database.CurrentTransaction);
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                var runner = context.GetService<Neo4jTransactionManager>().CurrentDriverTransaction!;
                await SeedEdgeAsync(scope, id, "uncommitted", 1, runner);
                await using (var enumerator = query.AsAsyncEnumerable().GetAsyncEnumerator())
                {
                    Assert.True(await enumerator.MoveNextAsync());
                    Assert.Equal(id, enumerator.Current.Id);
                }
                Assert.Same(transaction, context.Database.CurrentTransaction);
                Assert.Equal(id, (await query.SingleAsync()).Id);
                await transaction.RollbackAsync();
            }
            context.ChangeTracker.Clear();
            Assert.Empty(await query.ToListAsync());
            await SeedEdgeAsync(scope, id, "committed", 2);
            await using (var enumerator = query.AsAsyncEnumerable().GetAsyncEnumerator())
                Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("committed", (await query.SingleAsync()).Role);
        }
        finally { await CleanupAsync(scope); }
    }

    private IdentifiedContext Context() => new(new DbContextOptionsBuilder<IdentifiedContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task EnsureSchemaAsync()
    {
        // Fixed fixture-only reviewed DDL, with names isolated from save and association fixtures.
        await ExecuteAsync($"CREATE CONSTRAINT graphbridge_identified_query_source IF NOT EXISTS FOR (n:{SourceLabel}) REQUIRE (n.tenant,n.source_key) IS UNIQUE", new { });
        await ExecuteAsync($"CREATE CONSTRAINT graphbridge_identified_query_target IF NOT EXISTS FOR (n:{TargetLabel}) REQUIRE (n.tenant,n.target_key) IS UNIQUE", new { });
        await ExecuteAsync($"CREATE CONSTRAINT graphbridge_identified_query_edge IF NOT EXISTS FOR ()-[r:{EdgeType}]-() REQUIRE r.edge_key IS UNIQUE", new { });
    }

    private async Task SeedEndpointsAsync(Scope scope, bool missingSourceKey = false)
    {
        await EnsureSchemaAsync();
        var parameters = Parameters(scope);
        if (missingSourceKey) parameters["sourceId"] = null!;
        await ExecuteAsync($"CREATE (:{SourceLabel} {{tenant:$runId,source_key:$sourceId}}), (:{TargetLabel} {{tenant:$runId,target_key:$targetId}})", parameters);
    }

    private async Task SeedEdgeAsync(Scope scope, Guid? id, string? role, int rank, IAsyncQueryRunner? runner = null, string type = EdgeType)
    {
        var parameters = Parameters(scope);
        parameters["edgeId"] = id?.ToString("D")!;
        parameters["role"] = role!;
        parameters["rank"] = (long)rank;
        var text = $"MATCH (s:{SourceLabel} {{tenant:$runId}}), (t:{TargetLabel} {{tenant:$runId}}) CREATE (s)-[r:{type}]->(t) SET r.edge_key=$edgeId,r.role=$role,r.rank=$rank,r.weight=123,r.version=1";
        if (runner is null) await ExecuteAsync(text, parameters);
        else await (await runner.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task ExecuteAsync(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task CleanupAsync(Scope scope)
    {
        foreach (var type in new[] { EdgeType, CorruptType })
        {
            await ExecuteAsync($"MATCH (s:{SourceLabel} {{tenant:$runId}})-[r:{type}]->(t:{TargetLabel} {{tenant:$runId}}) DELETE r", Parameters(scope));
            await ExecuteAsync($"MATCH (s:{OtherLabel} {{tenant:$runId}})-[r:{type}]->(t:{OtherLabel} {{tenant:$runId}}) DELETE r", Parameters(scope));
        }
        foreach (var label in new[] { SourceLabel, TargetLabel, OtherLabel })
            await ExecuteAsync($"MATCH (n:{label} {{tenant:$runId}}) DELETE n", Parameters(scope));
    }

    private static Dictionary<string, object> Parameters(Scope scope) => new()
    {
        ["runId"] = scope.RunId, ["sourceId"] = scope.SourceId.ToString("D"), ["targetId"] = scope.TargetId.ToString("D")
    };
    private sealed record Scope(string RunId, Guid SourceId, Guid TargetId)
    {
        public static Scope New() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid());
    }

    public sealed class IdentifiedContext(DbContextOptions<IdentifiedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) => Configure(model, EdgeType);
    }
    public sealed class NoSchemaContext(DbContextOptions<NoSchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) => Configure(model, NoSchemaType);
    }
    public sealed class CorruptContext(DbContextOptions<CorruptContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) => Configure(model, CorruptType);
    }

    private static void Configure(ModelBuilder model, string type)
    {
        var source = model.Entity<Source>();
        source.ToGraphNode(SourceLabel);
        source.HasKey(e => new { e.Tenant, e.Id });
        source.Property(e => e.Tenant).HasGraphPropertyName("tenant");
        source.Property(e => e.Id).HasGraphPropertyName("source_key").ValueGeneratedNever();
        var target = model.Entity<Target>();
        target.ToGraphNode(TargetLabel);
        target.HasKey(e => new { e.Tenant, e.Id });
        target.Property(e => e.Tenant).HasGraphPropertyName("tenant");
        target.Property(e => e.Id).HasGraphPropertyName("target_key").ValueGeneratedNever();
        var edge = model.Entity<Edge>();
        edge.HasKey(e => e.Id);
        edge.Property(e => e.Id).HasGraphPropertyName("edge_key").ValueGeneratedNever();
        edge.Property(e => e.Role).HasGraphPropertyName("role");
        edge.Property(e => e.Rank).HasGraphPropertyName("rank");
        edge.Property(e => e.Weight).HasGraphPropertyName("weight").HasConversion(v => (long)(v * 100), v => v / 100m);
        edge.Property(e => e.Version).HasGraphPropertyName("version").IsRowVersion();
        edge.Property<string?>("Note").HasGraphPropertyName("note");
        edge.HasOne(e => e.Source).WithMany(e => e.Edges).HasForeignKey(e => new { e.SourceTenant, e.SourceId });
        edge.HasOne(e => e.Target).WithMany(e => e.Edges).HasForeignKey(e => new { e.TargetTenant, e.TargetId });
        edge.ToGraphIdentifiedRelationship(type, nameof(Edge.Id),
            [nameof(Edge.SourceTenant), nameof(Edge.SourceId)], [nameof(Edge.TargetTenant), nameof(Edge.TargetId)]);
    }

    public sealed class Source
    {
        public string Tenant { get; set; } = "";
        public Guid Id { get; set; }
        public List<Edge> Edges { get; } = [];
    }
    public sealed class Target
    {
        public string Tenant { get; set; } = "";
        public Guid Id { get; set; }
        public List<Edge> Edges { get; } = [];
    }
    public sealed class Edge
    {
        public Guid Id { get; set; }
        public string SourceTenant { get; set; } = "";
        public Guid SourceId { get; set; }
        public string TargetTenant { get; set; } = "";
        public Guid TargetId { get; set; }
        public string? Role { get; set; }
        public int Rank { get; set; }
        public decimal Weight { get; set; }
        public long Version { get; set; }
        public Source Source { get; set; } = null!;
        public Target Target { get; set; } = null!;
    }
}
