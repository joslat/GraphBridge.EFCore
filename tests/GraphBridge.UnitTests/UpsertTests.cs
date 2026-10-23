using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using static GraphBridge.UnitTests.SaveChangesFailureTests;

namespace GraphBridge.UnitTests;

// ADR 0051. Scripted protocol checks the client side only; the live suite establishes MERGE behavior on the server.
public sealed class UpsertTests
{
    [Fact]
    public async Task One_constrained_MERGE_carries_converted_create_and_update_maps_and_counts_from_the_summary()
    {
        var script = new Script { NodesCreated = 1 };
        await using var db = Context(script);
        var first = Person("ada", Mood.Calm, "a@example.org");
        var second = Person("bob", Mood.Busy, null);
        var result = await db.People.ExecuteUpsertAsync([first, second]);

        Assert.Equal(new GraphUpsertResult(1, 1, new GraphWriteCounters(1, 0, 0, 0, 0, 0, 0)), result);
        Assert.Equal(2, script.Commands.Count);
        Assert.StartsWith("SHOW CONSTRAINTS", script.Commands[0]);
        Assert.Equal("UNWIND $__gb_upsert_rows AS row\nMERGE (n:`UpsertPerson` {`person_key`: row.key.`person_key`})\n"
            + "ON CREATE SET n += row.create\nON MATCH SET n += row.update\nRETURN count(n) AS affected", script.Commands[1]);
        var rows = (Dictionary<string, object?>[])script.Parameters[1]![Neo4jUpsertExecutor.RowsParameter];
        Assert.Equal(first.Id.ToString("D"), ((Dictionary<string, object?>)rows[0]["key"]!)["person_key"]);
        var create = (Dictionary<string, object?>)rows[1]["create"]!;
        Assert.Equal(new[] { "Mood", "Name", "person_key" }, create.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Busy", create["Mood"]);
        // On match a null removes the property, so the update map keeps it; on create it is simply absent.
        var update = (Dictionary<string, object?>)rows[1]["update"]!;
        Assert.Equal(new[] { "Email", "Mood", "Name" }, update.Keys.Order(StringComparer.Ordinal));
        Assert.Null(update["Email"]);
        Assert.Equal((1, 0), (script.Commits, script.Rollbacks));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task The_selector_narrows_what_an_existing_node_receives()
    {
        var script = new Script();
        await using var db = Context(script);
        await db.People.ExecuteUpsertAsync([Person("ada", Mood.Calm, "a@example.org")], p => new { p.Name });
        var row = ((Dictionary<string, object?>[])script.Parameters[1]![Neo4jUpsertExecutor.RowsParameter])[0];
        Assert.Equal(new[] { "Name" }, ((Dictionary<string, object?>)row["update"]!).Keys);

        script.Commands.Clear(); script.Parameters.Clear();
        await db.People.ExecuteUpsertAsync([Person("ada", Mood.Calm, null)], p => p.Mood);
        row = ((Dictionary<string, object?>[])script.Parameters[1]![Neo4jUpsertExecutor.RowsParameter])[0];
        Assert.Equal(new[] { "Mood" }, ((Dictionary<string, object?>)row["update"]!).Keys);

        // An empty selector inserts missing nodes and leaves existing ones unchanged.
        script.Commands.Clear(); script.Parameters.Clear();
        await db.People.ExecuteUpsertAsync([Person("ada", Mood.Calm, null)], p => new { });
        Assert.DoesNotContain("ON MATCH", script.Commands[1]);
        row = ((Dictionary<string, object?>[])script.Parameters[1]![Neo4jUpsertExecutor.RowsParameter])[0];
        Assert.False(row.ContainsKey("update"));

        Assert.Contains("part of the key", (await Assert.ThrowsAsync<ArgumentException>(() => db.People.ExecuteUpsertAsync([Person("x", Mood.Calm, null)], p => p.Id))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => db.People.ExecuteUpsertAsync([Person("x", Mood.Calm, null)], p => p.Name.Length));
        await Assert.ThrowsAsync<ArgumentException>(() => db.People.ExecuteUpsertAsync([Person("x", Mood.Calm, null)], p => "constant"));
    }

    [Fact]
    public async Task Invalid_input_is_refused_before_anything_is_sent()
    {
        var script = new Script();
        await using var db = Context(script);
        var duplicate = Person("ada", Mood.Calm, null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.People.ExecuteUpsertAsync([duplicate, Person("bob", Mood.Calm, null), duplicate]));
        Assert.Contains("Entities 0 and 2", error.Message);
        Assert.DoesNotContain(duplicate.Id.ToString(), error.Message);
        Assert.Contains("client-supplied keys", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.People.ExecuteUpsertAsync([new UpsertPerson { Name = "no key" }]))).Message);
        var tooMany = Enumerable.Range(0, 10_001).Select(i => Person("p" + i, Mood.Calm, null));
        Assert.Contains("at most 10000", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.People.ExecuteUpsertAsync(tooMany))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => db.People.ExecuteUpsertAsync([null!]));
        Assert.Equal(new GraphUpsertResult(0, 0, GraphWriteCounters.None), await db.People.ExecuteUpsertAsync([]));
        Assert.Empty(script.Commands);
        Assert.Equal(0, script.Sessions);
    }

    [Fact]
    public async Task A_missing_constraint_is_refused_before_the_write_and_rolls_back()
    {
        var script = new Script { Constrained = false };
        await using var db = Context(script);
        Assert.Contains("uniqueness constraint", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.People.ExecuteUpsertAsync([Person("ada", Mood.Calm, null)]))).Message);
        Assert.Single(script.Commands);
        Assert.Equal(0, script.Commits);
    }

    [Fact]
    public async Task Concurrency_tokens_are_refused_until_their_own_design()
    {
        var script = new Script();
        await using var versioned = new ProviderModelContext(script.Driver, b =>
        {
            var entity = b.Entity<UpsertPerson>().ToGraphNode("UpsertPerson");
            entity.Property(p => p.Id).HasGraphPropertyName("person_key");
            entity.Property(p => p.Name).IsConcurrencyToken();
        });
        Assert.Contains("concurrency tokens", (await Assert.ThrowsAsync<NotSupportedException>(() =>
            versioned.Set<UpsertPerson>().ExecuteUpsertAsync([Person("ada", Mood.Calm, null)]))).Message);
        Assert.Empty(script.Commands);
    }

    [Fact]
    public async Task An_application_transaction_is_borrowed_and_left_for_the_caller_to_commit()
    {
        var script = new Script();
        await using var db = Context(script);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.People.ExecuteUpsertAsync([Person("ada", Mood.Calm, null)]);
        Assert.Equal((0, 1), (script.Commits, script.Sessions));
        await transaction.CommitAsync();
        Assert.Equal(1, script.Commits);
    }

    [Fact]
    public async Task Counters_reach_the_upsert_span()
    {
        var script = new Script { NodesCreated = 2 };
        await using var db = Context(script);
        using var parent = new Activity("upsert-counters").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans);
        await db.People.ExecuteUpsertAsync([Person("ada", Mood.Calm, null), Person("bob", Mood.Calm, null)]);
        var upsert = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.UpsertActivityName);
        Assert.Equal(2, upsert.GetTagItem("graphbridge.counters.nodes_created"));
    }

    private static UpsertPerson Person(string name, Mood mood, string? email) => new() { Id = Guid.NewGuid(), Name = name, Mood = mood, Email = email };

    private static UpsertContext Context(Script script) => new(new DbContextOptionsBuilder<UpsertContext>().UseNeo4j(script.Driver, "neo4j").Options);

    public enum Mood { Calm, Busy }

    public sealed class UpsertPerson
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Mood Mood { get; set; }
        public string? Email { get; set; }
    }

    public sealed class UpsertContext(DbContextOptions<UpsertContext> options) : DbContext(options)
    {
        public DbSet<UpsertPerson> People => Set<UpsertPerson>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            var person = model.Entity<UpsertPerson>().ToGraphNode("UpsertPerson");
            person.Property(p => p.Id).HasGraphPropertyName("person_key").ValueGeneratedNever();
            person.Property(p => p.Mood).HasConversion<string>();
        }
    }

    internal sealed class Script
    {
        public IDriver Driver { get; }
        public List<string> Commands { get; } = [];
        public List<IDictionary<string, object>?> Parameters { get; } = [];
        public bool Constrained { get; init; } = true;
        public int NodesCreated { get; init; }
        public int Sessions, Commits, Rollbacks;

        public Script()
        {
            var transaction = ProtocolProxy.For<IAsyncTransaction>((method, args) => method.Name switch
            {
                "RunAsync" => Task.FromResult(Run((string)args![0]!, args.Length > 1 ? args[1] as IDictionary<string, object> : null)),
                "CommitAsync" => Commit(),
                "RollbackAsync" => Rollback(),
                "DisposeAsync" => ValueTask.CompletedTask,
                _ => throw new InvalidOperationException(method.Name)
            });
            var session = ProtocolProxy.For<IAsyncSession>((method, _) => method.Name switch
            {
                "BeginTransactionAsync" => Task.FromResult(transaction), "DisposeAsync" => ValueTask.CompletedTask,
                _ => throw new InvalidOperationException(method.Name)
            });
            Driver = ProtocolProxy.For<IDriver>((method, _) =>
            {
                if (method.Name != "AsyncSession") throw new InvalidOperationException(method.Name);
                Sessions++; return session;
            });
        }

        private Task Commit() { Commits++; return Task.CompletedTask; }
        private Task Rollback() { Rollbacks++; return Task.CompletedTask; }

        private IResultCursor Run(string command, IDictionary<string, object>? parameters)
        {
            Commands.Add(command);
            Parameters.Add(parameters);
            if (command.StartsWith("SHOW CONSTRAINTS", StringComparison.Ordinal))
                return Cursor(Constrained
                    ? [new() { ["entityType"] = "NODE", ["type"] = "NODE_PROPERTY_UNIQUENESS", ["labelsOrTypes"] = new List<string> { "UpsertPerson" }, ["properties"] = new List<string> { "person_key" } }]
                    : [], null);
            var rows = ((Dictionary<string, object?>[])parameters![Neo4jUpsertExecutor.RowsParameter]).Length;
            var counters = ProtocolProxy.For<ICounters>((method, _) => method.Name switch
            {
                "get_NodesCreated" => NodesCreated, "get_ContainsUpdates" => NodesCreated > 0,
                _ when method.ReturnType == typeof(int) => 0,
                _ => throw new InvalidOperationException(method.Name)
            });
            var summary = ProtocolProxy.For<IResultSummary>((method, _) => method.Name == "get_Counters" ? counters : throw new InvalidOperationException(method.Name));
            return Cursor([new() { ["affected"] = (long)rows }], summary);
        }

        private static IResultCursor Cursor(IReadOnlyList<Dictionary<string, object?>> rows, IResultSummary? summary)
        {
            var index = -1;
            return ProtocolProxy.For<IResultCursor>((method, _) => method.Name switch
            {
                "FetchAsync" => Task.FromResult(++index < rows.Count),
                "get_Current" => ProtocolProxy.For<IRecord>((property, args) => property.Name switch
                {
                    "get_Item" => rows[index][(string)args![0]!],
                    _ => throw new InvalidOperationException(property.Name)
                }),
                "ConsumeAsync" when summary is not null => Task.FromResult(summary),
                _ => throw new InvalidOperationException(method.Name)
            });
        }
    }
}
