using System.Reflection;
using GraphBridge.EntityFrameworkCore.Transport;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>
/// Exercises the production database service through EF SaveChangesAsync and actual tracked entries.
/// Scripted protocol responses verify client state/lifecycle only: no graph storage, server rollback,
/// constraints, Cypher execution or unknown commit outcome is simulated or proven here.
/// </summary>
public sealed class SaveChangesFailureTests
{
    [Theory]
    [InlineData(EntityState.Added, null)]
    [InlineData(EntityState.Added, true)]
    [InlineData(EntityState.Added, false)]
    [InlineData(EntityState.Modified, null)]
    [InlineData(EntityState.Modified, true)]
    [InlineData(EntityState.Modified, false)]
    [InlineData(EntityState.Deleted, null)]
    [InlineData(EntityState.Deleted, true)]
    [InlineData(EntityState.Deleted, false)]
    public async Task Sync_save_rejects_before_transport_preserves_pending_state_and_allows_async_save(
        EntityState state, bool? acceptAllChangesOnSuccess)
    {
        var script = new SaveScript();
        await using var context = Context(script);
        var person = new Person { Id = Guid.NewGuid(), Name = "before", Version = state == EntityState.Added ? 0 : 7 };
        context.Entry(person).State = state == EntityState.Added ? EntityState.Added : EntityState.Unchanged;
        if (state == EntityState.Modified) person.Name = "after";
        if (state == EntityState.Deleted) context.Remove(person);

        var error = Assert.Throws<NotSupportedException>(() =>
        {
            if (acceptAllChangesOnSuccess is { } accept) context.SaveChanges(accept);
            else context.SaveChanges();
        });

        Assert.Contains("Use SaveChangesAsync", error.Message);
        Assert.Empty(script.Calls);
        AssertPending(context, person, state, state == EntityState.Added ? 0 : 7);
        if (state == EntityState.Modified)
        {
            Assert.Equal("before", context.Entry(person).Property(p => p.Name).OriginalValue);
            Assert.Equal("after", person.Name);
        }
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.Equal(state == EntityState.Deleted ? EntityState.Detached : EntityState.Unchanged, context.Entry(person).State);
    }

    [Fact]
    public async Task Sync_rejection_leaves_an_existing_async_transaction_under_caller_control()
    {
        var script = new SaveScript();
        await using var context = Context(script);
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Add(new Person { Name = "pending" });
        var before = script.Calls.ToArray();

        Assert.Throws<NotSupportedException>(() => context.SaveChanges());

        Assert.Equal(before, script.Calls);
        Assert.Same(transaction, context.Database.CurrentTransaction);
        await transaction.RollbackAsync();
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
    }

    [Fact]
    public void Unchanged_context_sync_save_returns_zero_through_EF_shortcut_without_transport()
    {
        var script = new SaveScript();
        using var context = Context(script);
        context.Attach(new Person { Id = Guid.NewGuid(), Name = "unchanged", Version = 7 });

        Assert.Equal(0, context.SaveChanges());
        Assert.Equal(0, context.SaveChanges(true));
        Assert.Equal(0, context.SaveChanges(false));
        Assert.Empty(script.Calls);
    }

    [Fact]
    public async Task Later_command_failure_attempts_rollback_without_publishing_earlier_generated_values_or_accepting_entries()
    {
        var rejected = new ClientException("scripted command rejected");
        var script = new SaveScript { MutationFailureAt = 2, MutationFailure = rejected };
        await using var context = Context(script);
        var first = new Person { Name = "first" };
        var second = new Person { Name = "second" };
        context.AddRange(first, second);

        var observed = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Same(rejected, observed.InnerException);
        Assert.Equal(2, observed.Entries.Count);
        Assert.Contains(observed.Entries, entry => ReferenceEquals(entry.Entity, first));
        Assert.Contains(observed.Entries, entry => ReferenceEquals(entry.Entity, second));
        Assert.Equal(new[] { "first", "second" }, script.Mutations.Select(AddedName));
        AssertPending(context, first, EntityState.Added, version: 0);
        AssertPending(context, second, EntityState.Added, version: 0);
        AssertClosed(context, script, commit: 0, rollback: 1, mutations: 2);
    }

    [Fact]
    public async Task Zero_mutation_count_reports_the_intended_entry_and_keeps_original_version_and_modified_state()
    {
        // First count belongs to the node lock; the second belongs to the actual update.
        var script = new SaveScript { ZeroCountAt = 2 };
        await using var context = Context(script);
        var person = new Person { Id = Guid.NewGuid(), Name = "before", Version = 7 };
        context.Attach(person);
        person.Name = "after";

        var observed = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());

        Assert.Same(person, Assert.Single(observed.Entries).Entity);
        Assert.Contains("SET n.`__graphbridge_lock` = true REMOVE n.`__graphbridge_lock`", script.Mutations[0].Text);
        var changes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(script.Mutations[1].Parameters["changes"]);
        Assert.Equal(8L, changes["version"]);
        AssertPending(context, person, EntityState.Modified, version: 7);
        Assert.Equal("before", context.Entry(person).Property(p => p.Name).OriginalValue);
        Assert.True(context.Entry(person).Property(p => p.Name).IsModified);
        AssertClosed(context, script, commit: 0, rollback: 1, mutations: 2);
    }

    [Fact]
    public async Task Commit_io_failure_preserves_the_original_error_without_retry_or_EF_acceptance()
    {
        var unavailable = new IOException("scripted commit response unavailable");
        var script = new SaveScript
        {
            CommitFailure = unavailable,
            TransactionDisposeFailure = new InvalidOperationException("secondary transaction disposal failure"),
            SessionDisposeFailure = new InvalidOperationException("secondary session disposal failure")
        };
        await using var context = Context(script);
        var person = new Person { Name = "pending" };
        context.Add(person);

        var observed = await Assert.ThrowsAsync<IOException>(() => context.SaveChangesAsync());

        Assert.Same(unavailable, observed);
        AssertPending(context, person, EntityState.Added, version: 0);
        AssertClosed(context, script, commit: 1, rollback: 0, mutations: 1);
        // This is a failed client commit response, not evidence that a real server did or did not commit.
    }

    [Fact]
    public async Task Successful_commit_followed_by_cleanup_failure_does_not_publish_or_accept_and_clears_manager()
    {
        var cleanup = new IOException("scripted session disposal failure after successful commit response");
        var script = new SaveScript { SessionDisposeFailure = cleanup };
        await using var context = Context(script);
        var person = new Person { Name = "pending" };
        context.Add(person);

        var observed = await Assert.ThrowsAsync<IOException>(() => context.SaveChangesAsync());

        Assert.Same(cleanup, observed);
        AssertPending(context, person, EntityState.Added, version: 0);
        AssertClosed(context, script, commit: 1, rollback: 0, mutations: 1);
    }

    [Fact]
    public async Task Explicit_transaction_command_failure_aborts_the_whole_transaction_and_never_owns_commit()
    {
        var rejected = new ClientException("scripted explicit transaction command rejection");
        var script = new SaveScript { MutationFailureAt = 2, MutationFailure = rejected };
        await using var context = Context(script);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var first = new Person { Name = "first" };
        var second = new Person { Name = "second" };
        context.AddRange(first, second);

        var observed = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Same(rejected, observed.InnerException);
        AssertPending(context, first, EntityState.Added, version: 0);
        AssertPending(context, second, EntityState.Added, version: 0);
        AssertClosed(context, script, commit: 0, rollback: 1, mutations: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
    }

    [Fact]
    public async Task Missing_schema_response_aborts_before_any_mutation_or_generated_value_publication()
    {
        var script = new SaveScript { HasConstraint = false };
        await using var context = Context(script);
        var person = new Person { Name = "pending" };
        context.Add(person);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Contains("existing uniqueness constraint", observed.Message);
        Assert.Contains(":SaveFailurePerson", observed.Message);
        Assert.Contains("appId", observed.Message);
        AssertPending(context, person, EntityState.Added, version: 0);
        AssertClosed(context, script, commit: 0, rollback: 1, mutations: 0);
    }

    [Fact]
    public async Task Successful_save_control_reports_entry_count_publishes_versions_and_accepts_only_after_commit()
    {
        var script = new SaveScript();
        await using var context = Context(script);
        var first = new Person { Name = "first" };
        var second = new Person { Name = "second" };
        context.AddRange(first, second);
        script.BeforeCommit = () =>
        {
            AssertPending(context, first, EntityState.Added, version: 0);
            AssertPending(context, second, EntityState.Added, version: 0);
        };

        Assert.Equal(2, await context.SaveChangesAsync());

        AssertPending(context, first, EntityState.Unchanged, version: 1);
        AssertPending(context, second, EntityState.Unchanged, version: 1);
        AssertClosed(context, script, commit: 1, rollback: 0, mutations: 2);
    }

    private static ProviderModelContext Context(SaveScript script)
    {
        var context = new ProviderModelContext(script.Driver, model =>
        {
            var person = model.Entity<Person>().ToGraphNode("SaveFailurePerson");
            person.Property(p => p.Id).HasGraphPropertyName("appId");
            person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
        });
        Assert.IsType<Neo4jDatabase>(context.GetService<IDatabase>());
        return context;
    }

    private static void AssertPending(DbContext context, Person person, EntityState state, long version)
    {
        Assert.Equal(state, context.Entry(person).State);
        Assert.Equal(version, person.Version);
        Assert.Equal(version, context.Entry(person).Property(p => p.Version).CurrentValue);
        Assert.Equal(version, context.Entry(person).Property(p => p.Version).OriginalValue);
        Assert.NotEqual(Guid.Empty, person.Id);
    }

    private static string AddedName(ScriptedMutation mutation)
        => (string)Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(mutation.Parameters["properties"])["Name"]!;

    private static void AssertClosed(DbContext context, SaveScript script, int commit, int rollback, int mutations)
    {
        var manager = context.GetService<Neo4jTransactionManager>();
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Null(manager.CurrentTransaction);
        Assert.Null(manager.CurrentDriverTransaction);
        Assert.Equal(1, script.Count("driver.AsyncSession"));
        Assert.Equal(1, script.Count("session.BeginTransactionAsync"));
        Assert.Equal(1, script.SchemaReads);
        Assert.Equal(mutations, script.Mutations.Count);
        Assert.Equal(commit, script.Count("transaction.CommitAsync"));
        Assert.Equal(rollback, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    public sealed class Person
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public long Version { get; set; }
    }

    private sealed record ScriptedMutation(string Text, IDictionary<string, object> Parameters);

    private sealed class SaveScript
    {
        public bool HasConstraint { get; init; } = true;
        public int MutationFailureAt { get; init; }
        public Exception? MutationFailure { get; init; }
        public int ZeroCountAt { get; init; }
        public Exception? CommitFailure { get; init; }
        public Exception? TransactionDisposeFailure { get; init; }
        public Exception? SessionDisposeFailure { get; init; }
        public Action? BeforeCommit { get; set; }
        public IDriver Driver { get; }
        public List<string> Calls { get; } = [];
        public List<ScriptedMutation> Mutations { get; } = [];
        public int SchemaReads { get; private set; }
        public int Count(string call) => Calls.Count(value => value == call);

        public SaveScript()
        {
            var transaction = ProtocolProxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                switch (method.Name)
                {
                    case "RunAsync":
                        return Run(args!);
                    case "CommitAsync":
                        BeforeCommit?.Invoke();
                        return Completion(method, CommitFailure);
                    case "RollbackAsync":
                        return Completion(method, null);
                    case "DisposeAsync":
                        return Completion(method, TransactionDisposeFailure);
                    default:
                        throw Unexpected(method);
                }
            });
            var session = ProtocolProxy.For<IAsyncSession>((method, _) =>
            {
                Calls.Add("session." + method.Name);
                return method.Name switch
                {
                    "BeginTransactionAsync" => Task.FromResult(transaction),
                    "DisposeAsync" => Completion(method, SessionDisposeFailure),
                    _ => throw Unexpected(method)
                };
            });
            Driver = ProtocolProxy.For<IDriver>((method, _) =>
            {
                Calls.Add("driver." + method.Name);
                return method.Name == "AsyncSession" ? session : throw Unexpected(method);
            });
        }

        private Task<IResultCursor> Run(object?[] args)
        {
            var text = (string)args[0]!;
            if (text.StartsWith("SHOW CONSTRAINTS", StringComparison.Ordinal))
            {
                SchemaReads++;
                return Task.FromResult(Cursor(HasConstraint ? [Record(new Dictionary<string, object>
                {
                    ["type"] = "NODE_PROPERTY_UNIQUENESS", ["entityType"] = "NODE",
                    ["labelsOrTypes"] = new List<string> { "SaveFailurePerson" },
                    ["properties"] = new List<string> { "appId" }
                })] : []));
            }
            Mutations.Add(new(text, (IDictionary<string, object>)args[1]!));
            if (Mutations.Count == MutationFailureAt)
                return Task.FromException<IResultCursor>(MutationFailure!);
            return Task.FromResult(Cursor([Record(new Dictionary<string, object>
            {
                ["count"] = Mutations.Count == ZeroCountAt ? 0L : 1L
            })]));
        }

        private static IResultCursor Cursor(IReadOnlyList<IRecord> records)
        {
            var index = -1;
            return ProtocolProxy.For<IResultCursor>((method, args) => method.Name switch
            {
                "GetAsyncEnumerator" => new RecordEnumerator(records, (CancellationToken)args![0]!),
                "FetchAsync" => Task.FromResult(++index < records.Count),
                "get_Current" => records[index],
                "ConsumeAsync" => Task.FromResult<IResultSummary>(null!),
                _ => throw Unexpected(method)
            });
        }

        private sealed class RecordEnumerator(IReadOnlyList<IRecord> records, CancellationToken cancellationToken)
            : IAsyncEnumerator<IRecord>
        {
            private int _index = -1;
            public IRecord Current => records[_index];
            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(++_index < records.Count);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static IRecord Record(IReadOnlyDictionary<string, object> values)
            => ProtocolProxy.For<IRecord>((method, args) => method.Name switch
            {
                "get_Item" => args![0] is string name ? values[name] : values.Values.ElementAt((int)args[0]!),
                "get_Keys" => values.Keys.ToList(),
                "get_Values" => values,
                _ => throw Unexpected(method)
            });

        private static object Completion(MethodInfo method, Exception? failure)
        {
            var task = failure is null ? Task.CompletedTask : Task.FromException(failure);
            // Pinned Driver 6.3.0 has Task operations and inherited ValueTask async disposal.
            if (method.ReturnType == typeof(Task)) return task;
            if (method.ReturnType == typeof(ValueTask)) return new ValueTask(task);
            throw new InvalidOperationException($"Unexpected driver async result shape: {method}");
        }
        private static Exception Unexpected(MethodInfo method)
            => new InvalidOperationException($"Unexpected scripted protocol operation: {method.Name}");
    }

    public class ProtocolProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T For<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var proxy = Create<T, ProtocolProxy>();
            ((ProtocolProxy)(object)proxy)._invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
    }
}
