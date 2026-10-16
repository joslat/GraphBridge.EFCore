using System.Text;
using System.Text.Json;
using GraphBridge.Samples.MoviesDemo;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Scaffolding;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class MoviesScaffoldTests(DisposableNeo4jFixture fixture)
{
    private static readonly string[] Labels = ["GraphBridgeMoviesPerson", "GraphBridgeMoviesMovie"];
    private static readonly string[] Types = ["GRAPHBRIDGE_MOVIES_ACTED_IN", "GRAPHBRIDGE_MOVIES_DIRECTED"];
    private static string Source => Path.Combine(AppContext.BaseDirectory, "MoviesSource");
    private static string FixtureId => Environment.GetEnvironmentVariable("GRAPHBRIDGE_FIXTURE_ID")
        ?? throw new InvalidOperationException("The disposable fixture marker is required.");

    [Neo4jIntegrationFact]
    public async Task Original_Movies_fixture_inspects_and_generates_reviewed_EF_artifacts_without_changing_data()
    {
        await MoviesFixture.VerifyFixtureAsync(fixture.Driver, fixture.Database, FixtureId);
        await MoviesFixture.SeedAsync(fixture.Driver, fixture.Database, FixtureId, Source);
        try
        {
            var original = await Data();
            var before = await Inspect();
            Assert.Equal(5, before.Nodes.Count);
            Assert.Equal(5, before.Relationships.Count);
            Assert.Empty(before.Schema.Constraints);
            Assert.Equal(2, before.Relationships.Count(edge => edge.ObservedParallel));
            Assert.All(before.Relationships.Where(edge => edge.RelationshipType == Types[0]),
                edge => Assert.Equal(GraphScaffoldValueKind.StringList, edge.Properties["roles"]));
            var readOnly = Generate(before, "graphbridge.readonly.policy.json");
            Assert.Null(readOnly.Fingerprint);
            var browsed = await MoviesWorkflow.BrowseAsync(fixture.Driver, fixture.Database);
            Assert.Equal(5, browsed.Count);
            Assert.Equal(4, browsed.Count(edge => edge.RelationshipType == Types[0]));
            Assert.Equal(original, await Data());
            var premature = GraphScaffoldCompiler.Compile(before, ReadPolicy("graphbridge.reviewed.policy.json"));
            Assert.False(premature.Success);
            Assert.Contains(premature.Diagnostics, d => d.Code == "MissingUniqueConstraint");

            await MoviesFixture.PrepareSchemaAsync(fixture.Driver, fixture.Database, FixtureId);
            // Exact existing owned schema may be explicitly checked again without redeployment.
            await MoviesFixture.PrepareSchemaAsync(fixture.Driver, fixture.Database, FixtureId);
            var after = await Inspect();
            Assert.Equal(4, after.Schema.Constraints.Count);
            Assert.Equal(4, after.Schema.Indexes.Count);
            var reviewedPolicy = ReadPolicy("graphbridge.reviewed.policy.json");
            var conflictReview = GraphScaffoldReview.Compare(before, reviewedPolicy, after, reviewedPolicy);
            Assert.False(conflictReview.Success); // Baseline really lacks the required constraints.
            Assert.True(conflictReview.Current.Success);
            Assert.Null(conflictReview.ModelsEquivalent);
            Assert.Contains(conflictReview.Baseline.Diagnostics, d => d.Code == "MissingUniqueConstraint");
            Assert.Contains(conflictReview.ObservationChanges, c => c.Path.StartsWith("/schema/constraints/", StringComparison.Ordinal));
            var stableReview = GraphScaffoldReview.Compare(after, reviewedPolicy,
                GraphScaffoldJson.ReadSnapshot(GraphScaffoldJson.WriteSnapshot(after)),
                GraphScaffoldJson.ReadPolicy(GraphScaffoldJson.WritePolicy(reviewedPolicy)));
            Assert.True(stableReview.Success);
            Assert.True(stableReview.ModelsEquivalent);
            Assert.True(stableReview.Current.RoundTripVerified);
            Assert.Empty(stableReview.ArtifactChanges);
            Assert.Equal(original, await Data());
            var tracked = Generate(after, "graphbridge.reviewed.policy.json");
            Assert.NotNull(tracked.Fingerprint);
            await using (var generatedContext = MoviesWorkflow.Context(fixture.Driver, fixture.Database))
                Assert.Equal(tracked.Fingerprint, generatedContext.Database.GetGraphSchemaManifest().Fingerprint);
            Assert.Contains(tracked.Diagnostics, d => d.Code == "ExplicitlyIgnoredPayload");
            var repeated = Generate(GraphScaffoldJson.ReadSnapshot(GraphScaffoldJson.WriteSnapshot(after)), "graphbridge.reviewed.policy.json");
            Assert.Equal(tracked.Files, repeated.Files);
            Assert.Equal(original, await Data());
            await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesFixture.SeedAsync(fixture.Driver, fixture.Database, FixtureId, Source));
            Assert.Equal(original, await Data());

            var result = await MoviesWorkflow.RunAsync(fixture.Driver, fixture.Database);
            Assert.Equal(2, result.MoviesRead);
            Assert.Equal(2, result.ParallelActingCredits);
            Assert.Equal(1, result.UpdatedEntries);
            Assert.True(result.ReferenceFixup);
            Assert.True(result.IdentityResolution);
            Assert.True(result.RolesPreserved);
            Assert.True(result.RollbackVerified);
            Assert.Equal(tracked.Fingerprint, result.SourceFingerprint);
            Assert.Equal(result.SourceFingerprint, result.TargetFingerprint);
            Assert.Contains("$p0", result.Cypher);
            Assert.Equal(original, await Data());

            // Optional CI export contains type/schema metadata and generated source only, never credentials or graph values.
            var capture = Environment.GetEnvironmentVariable("GRAPHBRIDGE_MOVIES_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(capture))
            {
                Directory.CreateDirectory(capture);
                await WriteNew(Path.Combine(capture, "before-schema.snapshot.json"), GraphScaffoldJson.WriteSnapshot(before));
                await WriteNew(Path.Combine(capture, "reviewed.snapshot.json"), GraphScaffoldJson.WriteSnapshot(after));
                await WriteNew(Path.Combine(capture, "conflict-review.json"), conflictReview.ToJson());
                await WriteNew(Path.Combine(capture, "stable-review.json"), stableReview.ToJson());
                GraphArtifactDirectory.Publish(readOnly, Path.Combine(capture, "generated-readonly"));
                GraphArtifactDirectory.Publish(tracked, Path.Combine(capture, "generated"));
            }
        }
        finally { await Cleanup(); }
    }

    [Neo4jIntegrationFact]
    public async Task Movies_workflow_and_DTO_browse_preserve_precancellation_without_transport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var driver = System.Reflection.DispatchProxy.Create<IDriver, MoviesNoTransportDriver>();
        var observer = (MoviesNoTransportDriver)(object)driver;
        var workflowError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MoviesWorkflow.RunAsync(driver, fixture.Database, cancellation.Token));
        Assert.Equal(cancellation.Token, workflowError.CancellationToken);
        var browseError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MoviesWorkflow.BrowseAsync(driver, fixture.Database, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, browseError.CancellationToken);
        Assert.Equal(0, observer.Calls);
    }

    [Neo4jIntegrationFact]
    public async Task Movies_setup_rejects_foreign_marker_and_preflights_all_constraint_names_before_writes()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesFixture.SeedAsync(fixture.Driver, fixture.Database, Guid.NewGuid().ToString("N"), Source));
        Assert.Empty(await Data());
        const string conflicting = "graphbridge_movies_directing_credit_id";
        await Execute($"CREATE CONSTRAINT {conflicting} FOR (n:GraphBridgeMoviesConflict) REQUIRE n.other IS UNIQUE");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesFixture.PrepareSchemaAsync(fixture.Driver, fixture.Database, FixtureId));
            var snapshot = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database,
                new GraphInspectionOptions(labels: [], relationshipTypes: []));
            Assert.Single(snapshot.Schema.Constraints, c => MoviesFixture.ConstraintNames.Contains(c.Name));
            Assert.Empty(await Data());
        }
        finally { await Execute($"DROP CONSTRAINT {conflicting}"); }
    }

    private async Task<GraphScaffoldSnapshot> Inspect()
    {
        var snapshot = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database,
            new GraphInspectionOptions(labels: Labels, relationshipTypes: Types));
        // Scope actual observations to this sample so unrelated test schema cannot alter committed evidence.
        return new(snapshot.Capabilities,
            new(snapshot.Schema.Constraints.Where(c => MoviesFixture.ConstraintNames.Contains(c.Name)),
                snapshot.Schema.Indexes.Where(i => MoviesFixture.ConstraintNames.Contains(i.Name))),
            snapshot.Labels.Where(Labels.Contains), snapshot.RelationshipTypes.Where(Types.Contains),
            snapshot.Nodes, snapshot.Relationships, snapshot.SampleLimit);
    }

    private static GraphScaffoldPolicy ReadPolicy(string name) => GraphScaffoldJson.ReadPolicy(File.ReadAllText(Path.Combine(Source, name)));
    private static GraphArtifactResult Generate(GraphScaffoldSnapshot snapshot, string policy)
    {
        var compilation = GraphScaffoldCompiler.Compile(snapshot, ReadPolicy(policy));
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
        var result = GraphScaffoldArtifacts.Generate(compilation, snapshot.Capabilities);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result;
    }

    private async Task<string[]> Data()
    {
        await using var session = fixture.OpenSession();
        var nodes = await session.RunAsync("MATCH (n) WHERE n:GraphBridgeMoviesPerson OR n:GraphBridgeMoviesMovie RETURN properties(n) AS value ORDER BY coalesce(n.personId,n.movieId)");
        var result = await nodes.ToListAsync(r => JsonSerializer.Serialize(r["value"].As<Dictionary<string, object>>()));
        var edges = await session.RunAsync("MATCH ()-[r:GRAPHBRIDGE_MOVIES_ACTED_IN|GRAPHBRIDGE_MOVIES_DIRECTED]->() RETURN properties(r) AS value ORDER BY coalesce(r.actingCreditId,r.directingCreditId)");
        return [.. result, .. await edges.ToListAsync(r => JsonSerializer.Serialize(r["value"].As<Dictionary<string, object>>()))];
    }
    private async Task Cleanup()
    {
        await MoviesFixture.VerifyFixtureAsync(fixture.Driver, fixture.Database, FixtureId);
        await Execute("MATCH (:GraphBridgeMoviesPerson)-[r:GRAPHBRIDGE_MOVIES_ACTED_IN|GRAPHBRIDGE_MOVIES_DIRECTED]->(:GraphBridgeMoviesMovie) DELETE r");
        await Execute("MATCH (n:GraphBridgeMoviesPerson) DELETE n");
        await Execute("MATCH (n:GraphBridgeMoviesMovie) DELETE n");
        foreach (var name in MoviesFixture.ConstraintNames) await Execute($"DROP CONSTRAINT `{name}` IF EXISTS");
    }
    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }
    private static async Task WriteNew(string path, string text)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }
}

public class MoviesNoTransportDriver : System.Reflection.DispatchProxy
{
    public int Calls { get; private set; }
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("A pre-canceled sample must not access the driver.");
    }
}
