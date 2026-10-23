using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Neo4j.Driver;
using static GraphBridge.UnitTests.SaveChangesFailureTests;

namespace GraphBridge.UnitTests;

// Scripted protocol checks client boundaries only; live tests must establish actual Cypher/locking behavior.
public sealed class BulkMutationTests
{
    [Fact]
    public async Task Actual_EF_update_batches_snapshot_assignments_and_versions_without_touching_tracking()
    {
        var script = new Script();
        await using var context = Context(script);
        var pending = new Person { Id = Guid.NewGuid(), Name = "pending", Count = 50 };
        context.Add(pending);
        var replacement = "private-new-name";
        var delta = 4;
        var affected = await context.Set<Person>().Where(p => p.Count > 0).OrderBy(p => p.Id).Take(10)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, replacement).SetProperty(p => p.Count, p => p.Count + delta)
                .SetProperty(p => p.Previous, p => p.Name));

        Assert.Equal(2, affected);
        Assert.Equal(4, script.Commands.Count);
        Assert.All(script.Commands, c => Assert.DoesNotContain(replacement, c));
        Assert.All(script.MutationRows!, row =>
        {
            var changes = Assert.IsType<Dictionary<string, object?>>(row["changes"]);
            Assert.Equal(replacement, changes["Name"]);
            Assert.Equal(7L, changes["Count"]);
            Assert.Equal("before", changes["Previous"]);
            Assert.Equal(8L, changes["Version"]);
        });
        Assert.Same(pending, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Equal(EntityState.Added, context.Entry(pending).State);
        Assert.Equal("pending", pending.Name);
        Assert.Equal(1, script.Commits); Assert.Equal(0, script.Rollbacks);
    }

    [Fact]
    public async Task Delete_is_restrictive_and_success_keeps_a_borrowed_transaction_open()
    {
        var script = new Script();
        await using var context = Context(script);
        await using var transaction = await context.Database.BeginTransactionAsync();
        Assert.Equal(2, await context.Set<Person>().ExecuteDeleteAsync());
        Assert.Contains("DELETE n", script.Commands.Last());
        Assert.DoesNotContain("DETACH", script.Commands.Last());
        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Equal(0, script.Commits); Assert.Equal(0, script.Rollbacks);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Changed_locked_snapshot_aborts_borrowed_transaction_before_mutation()
    {
        var script = new Script { ChangeAfterSelection = true };
        await using var context = Context(script);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.Set<Person>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "new")));
        Assert.Null(script.MutationRows); Assert.Equal(1, script.Rollbacks); Assert.Equal(0, script.Commits);
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Required_null_sync_key_duplicate_and_computed_setters_reject_before_transport()
    {
        var script = new Script();
        await using var context = Context(script);
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Person>().ExecuteDelete());
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.Set<Person>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Id, Guid.NewGuid())));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.Set<Person>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "one").SetProperty(p => p.Name, "two")));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.Set<Person>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, p => p.Name.ToUpper())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Person>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, (string)null!)));
        Assert.Equal(0, script.Sessions); Assert.Empty(script.Commands);
    }

    [Fact]
    public async Task Overflow_rejects_before_mutation_and_zero_rows_commit_a_noop()
    {
        var script = new Script();
        await using var context = Context(script);
        await Assert.ThrowsAsync<OverflowException>(() => context.Set<Person>().ExecuteUpdateAsync(s => s.SetProperty(p => p.Count, p => p.Count + int.MaxValue)));
        Assert.Null(script.MutationRows); Assert.Equal(1, script.Rollbacks);
        script.Rows.Clear();
        Assert.Equal(0, await context.Set<Person>().ExecuteDeleteAsync());
        Assert.Equal(1, script.Commits);
    }

    [Fact]
    public async Task Pre_cancellation_opens_nothing_and_cancellation_after_mutation_rolls_back()
    {
        var script = new Script();
        await using var context = Context(script);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Set<Person>().ExecuteDeleteAsync(new CancellationToken(true)));
        Assert.Equal(0, script.Sessions);
        using var cancellation = new CancellationTokenSource();
        script.AfterMutation = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Set<Person>().ExecuteDeleteAsync(cancellation.Token));
        Assert.NotNull(script.MutationRows); Assert.Equal(1, script.Rollbacks); Assert.Equal(0, script.Commits);
    }

    [Fact]
    public async Task Oversized_candidate_set_rejects_without_silent_partial_mutation()
    {
        var script = new Script();
        await using var context = Context(script);
        var prototype = script.Rows[0];
        script.Rows.Clear();
        for (var i = 0; i <= Neo4jBulkExpression.MaxRows; i++)
            script.Rows.Add(new(prototype) { [Neo4jBulkExpression.IdentityAlias] = "node-" + i });
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Person>().ExecuteDeleteAsync());
        Assert.Null(script.MutationRows); Assert.Equal(1, script.Rollbacks); Assert.Equal(0, script.Commits);
        Assert.Equal(Neo4jBulkExpression.MaxRows + 1L, script.MaxUnpagedLimit);
    }

    internal static ProviderModelContext Context(Script script)
    {
        var context = new ProviderModelContext(script.Driver, b =>
        {
            var entity = b.Entity<Person>().ToGraphNode("BulkPerson");
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.Version).IsRowVersion();
        });
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Person))!);
        for (var i = 0; i < 2; i++)
        {
            var row = new Dictionary<string, object?> { [Neo4jBulkExpression.IdentityAlias] = "node-" + i };
            foreach (var property in binding.Properties)
                row["c" + property.Property.GetIndex()] = property.Name switch
                {
                    "Id" => Guid.NewGuid().ToString(), "Name" => "before", "Previous" => null, "Count" => 3L, "Version" => 7L,
                    _ => throw new InvalidOperationException()
                };
            script.Rows.Add(row);
        }
        script.NameAlias = "c" + binding.Properties.Single(p => p.Name == "Name").Property.GetIndex();
        return context;
    }

    public sealed class Person
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Previous { get; set; }
        public int Count { get; set; }
        public long Version { get; set; }
    }

    internal sealed class Script
    {
        public IDriver Driver { get; }
        public List<Dictionary<string, object?>> Rows { get; } = [];
        public List<string> Commands { get; } = [];
        public Dictionary<string, object?>[]? MutationRows { get; private set; }
        public string NameAlias { get; set; } = "";
        public bool ChangeAfterSelection { get; init; }
        public Action? AfterMutation { get; set; }
        public int Sessions, Commits, Rollbacks;
        public long MaxUnpagedLimit = -1;
        public Script()
        {
            var transaction = ProtocolProxy.For<IAsyncTransaction>((method, args) =>
            {
                switch (method.Name)
                {
                    case "RunAsync": return Task.FromResult(Run((string)args![0]!, args.Length > 1 ? args[1] as IDictionary<string, object> : null));
                    case "CommitAsync": Commits++; return Task.CompletedTask;
                    case "RollbackAsync": Rollbacks++; return Task.CompletedTask;
                    case "DisposeAsync": return ValueTask.CompletedTask;
                    default: throw new InvalidOperationException(method.Name);
                }
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
        private IResultCursor Run(string command, IDictionary<string, object>? parameters)
        {
            Commands.Add(command);
            if (command.StartsWith("SHOW CONSTRAINTS", StringComparison.Ordinal))
                return Cursor([new() { ["entityType"] = "NODE", ["type"] = "NODE_PROPERTY_UNIQUENESS",
                    ["labelsOrTypes"] = new List<string> { "BulkPerson" }, ["properties"] = new List<string> { "Id" } }]);
            if (command.Contains("RETURN count(n)", StringComparison.Ordinal))
            {
                MutationRows = (Dictionary<string, object?>[])parameters!["__gb_bulk_rows"];
                AfterMutation?.Invoke();
                return Cursor([new() { ["affected"] = (long)MutationRows.Length }], () => Task.FromResult(MutationSummary!));
            }
            if (parameters!.TryGetValue("__gb_bulk_limit", out var limit)) MaxUnpagedLimit = (long)limit;
            var rows = Rows.Select(r => new Dictionary<string, object?>(r)).ToArray();
            if (command.Contains("SET n.", StringComparison.Ordinal) && ChangeAfterSelection && rows.Length > 0)
                rows[0][NameAlias] = "concurrent";
            return Cursor(rows);
        }
        // ADR 0050: only the mutation's summary is consumed; consuming a selection or lock cursor is unexpected.
        internal IResultSummary? MutationSummary { get; set; }
        private static IResultCursor Cursor(IReadOnlyList<Dictionary<string, object?>> rows, Func<Task<IResultSummary>>? consume = null)
        {
            var index = -1;
            return ProtocolProxy.For<IResultCursor>((method, _) => method.Name switch
            {
                "FetchAsync" => Task.FromResult(++index < rows.Count),
                "ConsumeAsync" when consume is not null => consume(),
                "get_Current" => ProtocolProxy.For<IRecord>((property, args) => property.Name switch
                {
                    "get_Values" => rows[index], "get_Item" => rows[index][(string)args![0]!],
                    _ => throw new InvalidOperationException(property.Name)
                }),
                _ => throw new InvalidOperationException(method.Name)
            });
        }
    }
}
