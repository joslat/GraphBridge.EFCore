using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderSchemaTests(DisposableNeo4jFixture fixture)
{
    private const string SourceLabel = "GraphBridgeWitnessSource";
    private const string TargetLabel = "GraphBridgeWitnessTarget";
    private const string RelationshipType = "GRAPHBRIDGE_WITNESS_EDGE";

    [Neo4jIntegrationFact]
    public async Task Reviewed_Community_proposals_deploy_and_missing_ID_constraint_is_reported_as_drift()
    {
        await using var db = Context();
        var initial = await db.Database.InspectGraphSchemaAsync();
        Assert.Equal(GraphSchemaStatus.Fail, initial.Comparison.Status);
        var proposals = initial.Comparison.Proposals;
        Assert.Equal(3, proposals.Count(p => p.Kind == GraphSchemaProposalKind.UniquenessConstraint));
        Assert.Equal(2, proposals.Count(p => p.Kind == GraphSchemaProposalKind.RangeIndex));
        Assert.All(proposals, p => Assert.StartsWith("CREATE ", p.Cypher));
        var source = new Source { Id = Guid.NewGuid(), Name = "schema-witness" };
        var target = new Target { Id = Guid.NewGuid() };
        var parameters = new { source = source.Id.ToString("D"), target = target.Id.ToString("D") };
        try
        {
            // Explicitly reviewed disposable-fixture DDL, outside the read-only provider inspection API.
            foreach (var proposal in proposals) await Execute(proposal.Cypher);
            await Execute("CALL db.awaitIndexes(30)");
            var deployed = await db.Database.InspectGraphSchemaAsync();
            Assert.DoesNotContain(deployed.Comparison.Issues, i => i.Status == GraphSchemaStatus.Fail);
            Assert.Empty(deployed.Comparison.Proposals);
            Assert.Equal(initial.Manifest.Fingerprint, deployed.Manifest.Fingerprint);
            Assert.Equal(GraphSchemaStatus.Warn, deployed.Comparison.Status); // Community does not enforce property existence/types.

            // Exercise actual Cypher 5 SHOW aliases without changing the database's default dialect.
            var cypher5 = await db.Database.QueryCypherAsync(
                "CYPHER 5 SHOW CONSTRAINTS YIELD entityType, type, labelsOrTypes, properties RETURN entityType, type, labelsOrTypes, properties",
                new Dictionary<string, object?>(), r => new GraphSchemaConstraint(r["entityType"].As<string>(),
                    r["type"].As<string>(), r["labelsOrTypes"].As<List<string>>(), r["properties"].As<List<string>>()));
            Assert.Contains(cypher5, c => c.LabelsOrTypes.Contains(SourceLabel) && c.Type == "UNIQUENESS");
            Assert.Contains(cypher5, c => c.LabelsOrTypes.Contains(RelationshipType) && c.Type == "RELATIONSHIP_UNIQUENESS");
            var compiler = new GraphBindingCompiler();
            GraphKeyConstraintValidator.Validate([compiler.GetNode(db.Model.FindEntityType(typeof(Source))!),
                compiler.GetNode(db.Model.FindEntityType(typeof(Target))!)], cypher5);
            GraphKeyConstraintValidator.ValidateIdentifiedEdges([compiler.GetIdentifiedEdge(db.Model.FindEntityType(typeof(Edge))!)], cypher5);

            db.AddRange(source, target,
                new Edge { Id = Guid.NewGuid(), SourceId = source.Id, TargetId = target.Id, Rank = 1 },
                new Edge { Id = Guid.NewGuid(), SourceId = source.Id, TargetId = target.Id, Rank = 2 });
            Assert.Equal(4, await db.SaveChangesAsync());
            await using var fresh = Context();
            Assert.Equal(new[] { 1, 2 }, await fresh.Set<Edge>().Where(e => e.SourceId == source.Id).OrderBy(e => e.Rank).Select(e => e.Rank).ToArrayAsync());

            var edgeConstraint = Assert.Single(proposals, p => p.EntityType == "RELATIONSHIP" && p.Kind == GraphSchemaProposalKind.UniquenessConstraint);
            await Execute($"DROP CONSTRAINT `{edgeConstraint.Name}`");
            var drift = await fresh.Database.InspectGraphSchemaAsync();
            Assert.Contains(drift.Comparison.Issues, i => i.Code == "MissingIdentityConstraint" && i.Status == GraphSchemaStatus.Fail);
            Assert.Contains(drift.Comparison.Proposals, p => p.Name == edgeConstraint.Name);
            // Inspection did not repair drift implicitly.
            Assert.DoesNotContain(drift.Observation.Constraints, c => c.Name == edgeConstraint.Name);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.Set<Edge>().ToListAsync());
        }
        finally
        {
            await Execute($"MATCH (s:{SourceLabel} {{id:$source}})-[r:{RelationshipType}]->(t:{TargetLabel} {{id:$target}}) DELETE r", parameters);
            await Execute($"MATCH (n:{SourceLabel} {{id:$source}}) DELETE n", parameters);
            await Execute($"MATCH (n:{TargetLabel} {{id:$target}}) DELETE n", parameters);
            foreach (var proposal in proposals)
                await Execute($"DROP {(proposal.Kind == GraphSchemaProposalKind.RangeIndex ? "INDEX" : "CONSTRAINT")} `{proposal.Name}` IF EXISTS");
        }
    }

    [Neo4jIntegrationFact]
    public async Task Community_inspection_observes_actual_capabilities_without_DDL_or_tracking()
    {
        await using var db = Context();
        var before = await SchemaNames();
        var manifest = db.Database.GetGraphSchemaManifest();
        var result = await db.Database.InspectGraphSchemaAsync();
        Assert.Equal(fixture.Database, result.DatabaseName);
        Assert.Equal(fixture.ServerVersion, result.Capabilities.ServerVersion);
        Assert.Equal(fixture.ServerEdition, result.Capabilities.Edition, ignoreCase: true);
        Assert.False(result.Capabilities.CanShowCurrentGraphType);
        Assert.Null(result.Observation.GraphTypeSpecification);
        Assert.Equal(GraphSchemaCapabilitySupport.Unsupported, result.Capabilities.PropertyExistence);
        Assert.Equal(GraphSchemaCapabilitySupport.Supported, result.Capabilities.RelationshipUniqueness);
        Assert.Equal(manifest.Fingerprint, result.Manifest.Fingerprint);
        Assert.Equal(2, manifest.Nodes.Count);
        Assert.Single(manifest.Relationships);
        Assert.DoesNotContain(manifest.Relationships[0].Properties, p => p.Name is nameof(Edge.SourceId) or nameof(Edge.TargetId));
        Assert.Equal(before, await SchemaNames());
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        Assert.NotEmpty(result.Comparison.Proposals);
    }

    [Neo4jIntegrationFact]
    public async Task Metadata_cap_fails_without_partial_result_and_context_remains_usable()
    {
        await using var db = Context();
        // dbms.components contains at least kernel and Cypher rows on the pinned fixture.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.InspectGraphSchemaAsync(1));
        Assert.Contains("maxSchemaRows", error.Message);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.NotNull(await db.Database.InspectGraphSchemaAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Neo4jIntegrationFact]
    public async Task Inspection_borrows_active_EF_transaction_without_committing_pending_data()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var db = Context();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.QueryCypherAsync($"CREATE (n:{SourceLabel} {{marker:$marker}}) RETURN n.marker AS marker",
                new Dictionary<string, object?> { ["marker"] = marker }, r => r["marker"].As<string>());
            await db.Database.InspectGraphSchemaAsync();
            Assert.Same(transaction, db.Database.CurrentTransaction);
            var count = await db.Database.QueryCypherAsync($"MATCH (n:{SourceLabel} {{marker:$marker}}) RETURN count(n) AS count",
                new Dictionary<string, object?> { ["marker"] = marker }, r => r["count"].As<long>());
            Assert.Equal(1L, Assert.Single(count));
            await transaction.RollbackAsync();
        }
        await using var fresh = Context();
        Assert.Equal(0L, Assert.Single(await fresh.Database.QueryCypherAsync(
            $"MATCH (n:{SourceLabel} {{marker:$marker}}) RETURN count(n) AS count",
            new Dictionary<string, object?> { ["marker"] = marker }, r => r["count"].As<long>())));
    }

    [Neo4jIntegrationFact]
    public async Task Inspection_honors_selected_database_and_precancel_without_leaking_EF_transaction()
    {
        await using var missing = new DbContext(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver,
            "graphbridge_witness_missing_" + Guid.NewGuid().ToString("N")).Options);
        await Assert.ThrowsAnyAsync<Neo4jException>(() => missing.Database.InspectGraphSchemaAsync());
        Assert.Null(missing.Database.CurrentTransaction);
        await using var valid = Context();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => valid.Database.InspectGraphSchemaAsync(
            cancellationToken: new CancellationToken(true)));
        Assert.Null(valid.Database.CurrentTransaction);
        Assert.NotNull(await valid.Database.InspectGraphSchemaAsync());
    }

    private SchemaContext Context() => new(new DbContextOptionsBuilder<SchemaContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task<string[]> SchemaNames()
    {
        await using var session = fixture.OpenSession();
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name")).ToListAsync(r => "constraint:" + r["name"].As<string>());
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name RETURN name")).ToListAsync(r => "index:" + r["name"].As<string>());
        return constraints.Concat(indexes).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task Execute(string query, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        var cursor = parameters is null ? await session.RunAsync(query) : await session.RunAsync(query, parameters);
        await cursor.ConsumeAsync();
    }

    private sealed class SchemaContext(DbContextOptions<SchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var source = model.Entity<Source>().ToGraphNode(SourceLabel);
            source.Property(p => p.Id).HasGraphPropertyName("id");
            source.Property(p => p.Name).HasGraphPropertyName("name");
            source.HasIndex(p => p.Name);
            var target = model.Entity<Target>().ToGraphNode(TargetLabel);
            target.Property(p => p.Id).HasGraphPropertyName("id");
            var edge = model.Entity<Edge>();
            edge.HasOne<Source>().WithMany().HasForeignKey(p => p.SourceId);
            edge.HasOne<Target>().WithMany().HasForeignKey(p => p.TargetId);
            edge.ToGraphIdentifiedRelationship(RelationshipType, nameof(Edge.Id), [nameof(Edge.SourceId)], [nameof(Edge.TargetId)]);
            edge.Property(p => p.Id).HasGraphPropertyName("edge_id");
            edge.Property(p => p.Rank).HasGraphPropertyName("rank");
            edge.HasIndex(p => p.Rank);
        }
    }
    private sealed class Source { public Guid Id { get; set; } public string Name { get; set; } = ""; }
    private sealed class Target { public Guid Id { get; set; } }
    private sealed class Edge { public Guid Id { get; set; } public Guid SourceId { get; set; } public Guid TargetId { get; set; } public int Rank { get; set; } }
}
