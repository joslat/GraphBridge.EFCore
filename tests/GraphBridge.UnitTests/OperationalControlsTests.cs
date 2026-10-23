using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;
using static GraphBridge.UnitTests.FluentCypherLambdaTests;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>ADR 0050: query tags as Cypher comments, command timeouts and write counters.</summary>
public sealed class OperationalControlsTests
{
    [Fact]
    public void LINQ_tags_lead_the_command_as_one_comment_line_per_tag_line()
    {
        using var db = Context();
        Assert.StartsWith("// dashboard: members\n\nMATCH (n:`LambdaMember`)", db.Set<Member>().TagWith("dashboard: members").ToQueryString());
        Assert.StartsWith("// one\n// two\n\nMATCH", db.Set<Member>().TagWith("one").TagWith("two").ToQueryString());
        // Every line terminator starts a new comment line, so a tag can never continue as Cypher.
        var lines = db.Set<Member>().TagWith("first\nMATCH (n) DETACH DELETE n\r\nthird\x2028fourth\rfifth\u0085sixth\x2029seventh")
            .ToQueryString().Split('\n');
        Assert.Equal(new[] { "// first", "// MATCH (n) DETACH DELETE n", "// third", "// fourth", "// fifth", "// sixth", "// seventh", "" }, lines[..8]);
        Assert.StartsWith("MATCH (n:`LambdaMember`)", lines[8]);
        Assert.Matches(@"^// File: .*OperationalControlsTests\.cs:\d+\n\nMATCH", db.Set<Member>().TagWithCallSite().ToQueryString());
        Assert.StartsWith("MATCH", db.Set<Member>().ToQueryString());
    }

    [Fact]
    public void Fluent_tags_lead_the_command_and_snapshot_lines_count_from_the_first_clause()
    {
        using var db = Context();
        var club = Graph.Node<Club>();
        var query = db.Cypher().Match(club).Return(() => club.Value.Title).TagWith("report").TagWith("weekly\nclubs").Limit(5);
        var command = query.Inspect();
        Assert.Equal("// report\n// weekly\n// clubs\n\nMATCH (n0:`LambdaClub`)\nRETURN n0.`Title` AS c0\nLIMIT $p0", command.Text.Replace("\r", ""));
        var clauses = command.Semantics!.Clauses;
        Assert.Equal((5, 5), (clauses[0].FirstLine, clauses[0].LastLine));
        Assert.Equal(CypherClauseKind.Return, clauses[1].Kind);
        Assert.Equal(6, clauses[1].FirstLine);
        Assert.Throws<ArgumentException>(() => query.TagWith(" "));
        Assert.StartsWith("MATCH", db.Cypher().Match(club).Return(() => club.Value.Title).Limit(5).ToQueryString());
    }

    [Fact]
    public async Task Bulk_tags_mark_the_selection_and_mutation_but_not_the_schema_preflight()
    {
        var script = new BulkMutationTests.Script();
        await using var context = BulkMutationTests.Context(script);
        Assert.Equal(2, await context.Set<BulkMutationTests.Person>().TagWith("nightly cleanup").ExecuteDeleteAsync());
        Assert.StartsWith("SHOW CONSTRAINTS", script.Commands[0]);
        Assert.All(script.Commands.Skip(1), command => Assert.StartsWith("// nightly cleanup\n\n", command));
    }

    [Fact]
    public void Timeouts_flow_from_options_to_the_context_and_are_validated()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, "neo4j", neo4j => neo4j.CommandTimeout(TimeSpan.FromSeconds(5))).Options);
        Assert.Equal(TimeSpan.FromSeconds(5), db.Database.GetGraphCommandTimeout());
        db.Database.SetGraphCommandTimeout(TimeSpan.FromMilliseconds(250));
        Assert.Equal(TimeSpan.FromMilliseconds(250), db.Database.GetGraphCommandTimeout());
        db.Database.SetGraphCommandTimeout(null);
        Assert.Null(db.Database.GetGraphCommandTimeout());
        // A pooled context returns to the configured default.
        db.GetService<Neo4jTransactionManager>().ResetState();
        Assert.Equal(TimeSpan.FromSeconds(5), db.Database.GetGraphCommandTimeout());

        Assert.Throws<ArgumentOutOfRangeException>(() => db.Database.SetGraphCommandTimeout(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.Database.SetGraphCommandTimeout(TimeSpan.FromHours(25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DbContextOptionsBuilder().UseNeo4j(driver, "neo4j", neo4j => neo4j.CommandTimeout(TimeSpan.FromMilliseconds(-1))));
        using var plain = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        Assert.Null(plain.Database.GetGraphCommandTimeout());
        var club = Graph.Node<Club>();
        using var lambda = Context();
        Assert.Throws<ArgumentOutOfRangeException>(() => lambda.Cypher().Match(club).Return(() => club.Value.Title).WithTimeout(TimeSpan.Zero));
    }

    [Fact]
    public async Task Owned_transactions_carry_the_timeout_and_an_application_transaction_refuses_a_per_query_timeout()
    {
        var script = new NativeQueryTests.NativeScript();
        await using var db = NativeQueryTests.Context(script);
        await db.Database.QueryCypherAsync("RETURN 1 AS c0", new Dictionary<string, object?>(), r => r["c0"]);
        Assert.Empty(script.BeginArguments.Single());
        db.Database.SetGraphCommandTimeout(TimeSpan.FromSeconds(2));
        await db.Database.QueryCypherAsync("RETURN 1 AS c0", new Dictionary<string, object?>(), r => r["c0"]);
        Assert.IsType<Action<TransactionConfigBuilder>>(Assert.Single(script.BeginArguments[1]));

        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Single(script.BeginArguments[2]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Neo4jDatabaseFacadeExtensions.QueryCypherWithTimeoutAsync(
            db.Database, "RETURN 1 AS c0", new Dictionary<string, object?>(), r => r["c0"], 10, TimeSpan.FromSeconds(1), default));
        Assert.Contains("SetGraphCommandTimeout", error.Message);
        Assert.Equal(3, script.BeginArguments.Count);
    }

    [Fact]
    public async Task Write_counters_come_from_the_consumed_summary_and_reach_the_operation_activity()
    {
        var script = new NativeQueryTests.NativeScript { Summary = Summary(nodesCreated: 2, relationshipsCreated: 1, propertiesSet: 5) };
        await using var db = NativeQueryTests.Context(script);
        using var parent = new Activity("counters-native").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans);

        var counters = await db.Database.ExecuteCypherAsync("CREATE (:A)-[:R]->(:B)", new Dictionary<string, object?>());
        Assert.Equal(new GraphWriteCounters(2, 0, 1, 0, 5, 0, 0), counters);
        Assert.True(counters.ContainsUpdates);
        var operation = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.NativeQueryActivityName);
        Assert.Equal(2, operation.GetTagItem("graphbridge.counters.nodes_created"));
        Assert.Equal(1, operation.GetTagItem("graphbridge.counters.relationships_created"));
        Assert.Equal(5, operation.GetTagItem("graphbridge.counters.properties_set"));
        Assert.Equal(0, operation.GetTagItem("graphbridge.counters.nodes_deleted"));
        Assert.False(GraphWriteCounters.None.ContainsUpdates);
        Assert.Equal(new GraphWriteCounters(3, 1, 1, 0, 5, 1, 0), counters + new GraphWriteCounters(1, 1, 0, 0, 0, 1, 0));
    }

    [Fact]
    public async Task Bulk_counters_come_from_the_mutation_statement_and_not_the_lock()
    {
        var script = new BulkMutationTests.Script { MutationSummary = Summary(nodesDeleted: 2) };
        await using var context = BulkMutationTests.Context(script);
        using var parent = new Activity("counters-bulk").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans);

        // The script refuses ConsumeAsync on every cursor but the mutation's, so the lock's no-op SET is never counted.
        Assert.Equal(2, await context.Set<BulkMutationTests.Person>().ExecuteDeleteAsync());
        var bulk = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.BulkActivityName);
        Assert.Equal(2, bulk.GetTagItem("graphbridge.counters.nodes_deleted"));
        Assert.Equal(0, bulk.GetTagItem("graphbridge.counters.properties_set"));
    }

    [Fact]
    public void A_transaction_timeout_is_classified_without_replacing_the_driver_error()
    {
        using var activity = new Activity("timeout").Start();
        GraphBridgeDiagnostics.Fail(activity, new ClientException("Neo.ClientError.Transaction.TransactionTimedOutClientConfiguration", "timed out"));
        Assert.Equal("timeout", activity.GetTagItem("error.type"));
        using var other = new Activity("other").Start();
        GraphBridgeDiagnostics.Fail(other, new ClientException("Neo.ClientError.Statement.SyntaxError", "bad"));
        Assert.Equal("driver_error", other.GetTagItem("error.type"));
    }

    private static IResultSummary Summary(int nodesCreated = 0, int relationshipsCreated = 0, int propertiesSet = 0, int nodesDeleted = 0)
    {
        var counters = NativeQueryTests.Proxy.For<ICounters>((method, _) => method.Name switch
        {
            "get_NodesCreated" => nodesCreated, "get_RelationshipsCreated" => relationshipsCreated, "get_PropertiesSet" => propertiesSet,
            "get_NodesDeleted" => nodesDeleted,
            "get_ContainsUpdates" => nodesCreated + relationshipsCreated + propertiesSet + nodesDeleted > 0,
            _ when method.ReturnType == typeof(int) => 0,
            _ when method.ReturnType == typeof(bool) => false,
            _ => throw new NotSupportedException(method.Name)
        });
        return NativeQueryTests.Proxy.For<IResultSummary>((method, _) => method.Name == "get_Counters" ? counters : throw new NotSupportedException(method.Name));
    }
}
