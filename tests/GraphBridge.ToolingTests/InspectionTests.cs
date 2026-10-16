using System.Reflection;
using GraphBridge.Tooling.Scaffolding;
using Neo4j.Driver;

namespace GraphBridge.ToolingTests;

public sealed class InspectionTests
{
    [Fact]
    public async Task Repeated_physical_relationship_observation_does_not_prove_parallel_edges()
    {
        var protocol = new Protocol { DuplicateRelationship = true };
        var snapshot = await GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j");
        Assert.False(Assert.Single(snapshot.Relationships).ObservedParallel);
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Combined_metadata_text_budget_rejects_before_retaining_unbounded_evidence()
    {
        var protocol = new Protocol { OversizedText = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j"));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Inspection_returns_type_evidence_and_observed_parallelism_without_values_or_writes()
    {
        var protocol = new Protocol { Parallel = true };
        var snapshot = await GraphDatabaseInspector.InspectAsync(protocol.Driver, "chosen-db");
        Assert.Equal(GraphScaffoldValueKind.String, Assert.Single(snapshot.Nodes).Properties["name"]);
        Assert.Equal(2, snapshot.Relationships.Count);
        Assert.All(snapshot.Relationships, sample => Assert.True(sample.ObservedParallel));
        Assert.Null(snapshot.Schema.GraphTypeSpecification);
        Assert.DoesNotContain(protocol.Queries, query => query.Contains("GRAPH TYPE", StringComparison.Ordinal));
        Assert.All(protocol.Queries, query => Assert.True(query.StartsWith("CALL", StringComparison.Ordinal)
            || query.StartsWith("SHOW", StringComparison.Ordinal) || query.StartsWith("MATCH", StringComparison.Ordinal)));
        Assert.All(protocol.Queries.Where(query => query.StartsWith("MATCH", StringComparison.Ordinal)), query =>
        {
            Assert.True(query.IndexOf("LIMIT $limit", StringComparison.Ordinal) < query.IndexOf("RETURN", StringComparison.Ordinal));
            Assert.DoesNotContain("properties(n)", query);
            Assert.DoesNotContain("properties(r)", query);
        });
        Assert.All(protocol.Parameters, parameters => Assert.True((long)parameters["limit"] > 0));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Overlapping_label_samples_are_deduplicated_without_exporting_internal_ids()
    {
        var protocol = new Protocol { Labels = ["Person", "Actor"], NodeLabels = ["Actor", "Person"] };
        var snapshot = await GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j");
        Assert.Equal(2, snapshot.Labels.Count);
        Assert.Single(snapshot.Nodes);
        Assert.Equal(2, snapshot.Nodes[0].Labels.Count);
        Assert.Equal(2, protocol.Queries.Count(query => query.StartsWith("MATCH (n:", StringComparison.Ordinal)));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Constraint_inventory_retains_empty_labels_and_explicit_scope_avoids_unselected_sampling()
    {
        var protocol = new Protocol { SchemaOnlyLabel = "Empty", EmptySamples = true };
        var snapshot = await GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j",
            new(labels: ["Empty"], relationshipTypes: []));
        Assert.Equal(["Empty"], snapshot.Labels);
        Assert.Empty(snapshot.Nodes);
        Assert.Empty(snapshot.RelationshipTypes);
        Assert.Single(protocol.Queries, query => query.StartsWith("MATCH", StringComparison.Ordinal));
        Assert.Contains(protocol.Queries, query => query.StartsWith("MATCH (n:`Empty`)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Metadata_row_overflow_rolls_back_and_never_returns_a_partial_snapshot()
    {
        var protocol = new Protocol();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j", new(maxMetadataRows: 1)));
        Assert.Single(protocol.Queries);
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Per_element_property_overflow_rejects_instead_of_silently_truncating()
    {
        var protocol = new Protocol { PropertyCount = 65 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j"));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Total_budget_counts_raw_observations_even_when_nodes_would_be_deduplicated()
    {
        var protocol = new Protocol { Labels = ["Person", "Actor"], NodeLabels = ["Actor", "Person"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j", new(maxTotalSamples: 1)));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Unsafe_discovered_identifier_is_never_embedded_in_a_query()
    {
        var protocol = new Protocol { Labels = ["bad`) DELETE n //"] };
        await Assert.ThrowsAsync<ArgumentException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j"));
        Assert.DoesNotContain(protocol.Queries, query => query.StartsWith("MATCH", StringComparison.Ordinal));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Pre_cancelled_inspection_never_opens_a_session()
    {
        var protocol = new Protocol();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j", cancellationToken: new(true)));
        Assert.Empty(protocol.Calls);
    }

    [Fact]
    public async Task Cancellation_after_run_is_observed_before_fetch_and_cleanup_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var protocol = new Protocol { OnRun = cancellation.Cancel, CleanupFailure = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j", cancellationToken: cancellation.Token));
        Assert.DoesNotContain("cursor.FetchAsync", protocol.Calls);
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Read_failure_survives_all_cleanup_failures()
    {
        var error = new IOException("read failed");
        var protocol = new Protocol { ReadFailure = error, CleanupFailure = true };
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j")));
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Cleanup_failure_after_success_is_not_hidden()
    {
        var protocol = new Protocol { CleanupFailure = true };
        await Assert.ThrowsAsync<IOException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j"));
        protocol.AssertCleanup();
    }

    [Theory]
    [InlineData("5.12.0")]
    [InlineData("unknown")]
    public async Task Unsupported_type_observation_profile_rejects_before_schema_or_sampling(string version)
    {
        var protocol = new Protocol { Version = version };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GraphDatabaseInspector.InspectAsync(protocol.Driver, "neo4j"));
        Assert.Single(protocol.Queries);
        protocol.AssertCleanup();
    }

    private sealed class Protocol
    {
        public string Version { get; init; } = "2026.09.0";
        public string[] Labels { get; init; } = ["Person"];
        public string[] NodeLabels { get; init; } = ["Person"];
        public bool Parallel { get; init; }
        public bool DuplicateRelationship { get; init; }
        public bool OversizedText { get; init; }
        public bool EmptySamples { get; init; }
        public string? SchemaOnlyLabel { get; init; }
        public long PropertyCount { get; init; } = 1;
        public Exception? ReadFailure { get; init; }
        public bool CleanupFailure { get; init; }
        public Action? OnRun { get; init; }
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public List<IDictionary<string, object>> Parameters { get; } = [];
        public IDriver Driver { get; }
        public Protocol()
        {
            var transaction = Proxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                if (method.Name != "RunAsync") return Completion(method);
                var query = Assert.IsType<string>(args![0]);
                Queries.Add(query);
                Parameters.Add(Assert.IsAssignableFrom<IDictionary<string, object>>(args[1]));
                if (ReadFailure is not null) return Task.FromException<IResultCursor>(ReadFailure);
                OnRun?.Invoke();
                return Task.FromResult(Cursor(query));
            });
            var session = Proxy.For<IAsyncSession>((method, args) =>
            {
                Calls.Add("session." + method.Name);
                if (method.Name != "BeginTransactionAsync") return Completion(method);
                Assert.IsType<Action<TransactionConfigBuilder>>(args![0]);
                return Task.FromResult(transaction);
            });
            Driver = Proxy.For<IDriver>((method, args) =>
            {
                Calls.Add("driver." + method.Name);
                Assert.Equal("AsyncSession", method.Name);
                Assert.IsType<Action<SessionConfigBuilder>>(args![0]);
                return session;
            });
        }
        public void AssertCleanup()
        {
            Assert.Equal(1, Calls.Count(call => call == "transaction.RollbackAsync"));
            Assert.Equal(1, Calls.Count(call => call == "transaction.DisposeAsync"));
            Assert.Equal(1, Calls.Count(call => call == "session.DisposeAsync"));
            Assert.DoesNotContain("transaction.CommitAsync", Calls);
            Assert.DoesNotContain("driver.DisposeAsync", Calls);
        }
        private object Completion(MethodInfo method)
        {
            var result = CleanupFailure ? Task.FromException(new IOException("cleanup failed")) : Task.CompletedTask;
            return method.ReturnType == typeof(ValueTask) ? new ValueTask(result) : result;
        }
        private IResultCursor Cursor(string query)
        {
            var rows = Rows(query);
            var index = -1;
            return Proxy.For<IResultCursor>((method, _) =>
            {
                Calls.Add("cursor." + method.Name);
                return method.Name switch
                {
                    "FetchAsync" => Task.FromResult(++index < rows.Length),
                    "get_Current" => Proxy.For<IRecord>((member, _) => member.Name == "get_Values" ? rows[index] : throw new NotSupportedException(member.Name)),
                    "ConsumeAsync" => Task.FromResult<IResultSummary>(null!),
                    _ => throw new NotSupportedException(method.Name)
                };
            });
        }
        private Dictionary<string, object>[] Rows(string query)
        {
            if (OversizedText) return [new() { ["name"] = new string('x', 1048577) }];
            if (query.StartsWith("CALL dbms.components", StringComparison.Ordinal)) return
                [new() { ["name"] = "Neo4j Kernel", ["versions"] = new[] { Version }, ["edition"] = "community" },
                 new() { ["name"] = "Cypher", ["versions"] = new[] { "5", "25" }, ["edition"] = "community" }];
            if (query.StartsWith("SHOW CONSTRAINTS", StringComparison.Ordinal)) return SchemaOnlyLabel is null ? [] :
                [new() { ["name"] = "empty_key", ["type"] = "NODE_PROPERTY_UNIQUENESS", ["entityType"] = "NODE", ["labelsOrTypes"] = new[] { SchemaOnlyLabel }, ["properties"] = new[] { "id" }, ["propertyType"] = null! }];
            if (query.StartsWith("SHOW INDEXES", StringComparison.Ordinal)) return [];
            if (query.StartsWith("CALL db.labels", StringComparison.Ordinal)) return Labels.Select(label => new Dictionary<string, object> { ["label"] = label }).ToArray();
            if (query.StartsWith("CALL db.relationshipTypes", StringComparison.Ordinal)) return [new() { ["relationshipType"] = "KNOWS" }];
            if (EmptySamples) return [];
            if (query.StartsWith("MATCH (n:", StringComparison.Ordinal)) return
                [new() { ["nodeToken"] = "ephemeral-node", ["labels"] = NodeLabels, ["labelCount"] = (long)NodeLabels.Length,
                    ["propertyCount"] = PropertyCount, ["properties"] = new[] { new Dictionary<string, object> { ["name"] = "name", ["type"] = "STRING NOT NULL" } } }];
            var relationship = new Dictionary<string, object>
            {
                ["sourceLabels"] = new[] { "Person" }, ["sourceLabelCount"] = 1L,
                ["targetLabels"] = new[] { "Person" }, ["targetLabelCount"] = 1L,
                ["relationshipToken"] = "ephemeral-edge", ["sourceToken"] = "ephemeral-a", ["targetToken"] = "ephemeral-b", ["propertyCount"] = 0L, ["properties"] = Array.Empty<object>()
            };
            return DuplicateRelationship ? [relationship, relationship] : Parallel
                ? [relationship, new Dictionary<string, object>(relationship) { ["relationshipToken"] = "ephemeral-other-edge" }] : [relationship];
        }
    }
    public class Proxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T For<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var instance = Create<T, Proxy>();
            ((Proxy)(object)instance)._invoke = invoke;
            return instance;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
    }
}
