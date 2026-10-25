using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Transport;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace GraphBridge.UnitTests;

public sealed class NodeMutationTests
{
    [Fact]
    public void Added_entry_uses_real_EF_generated_Guid_parameterized_create_and_initial_version()
    {
        using var context = Context();
        var person = new Person { Name = "O'Reilly `$x` MATCH (n) DELETE n", Age = 31 };
        context.Add(person);
        Assert.NotEqual(Guid.Empty, person.Id);
        var mutation = Assert.Single(Plan(context, person));
        Assert.StartsWith("CREATE (n:`MutationPerson`)", mutation.Command.Text);
        Assert.DoesNotContain("O'Reilly", mutation.Command.Text);
        Assert.DoesNotContain("MERGE", mutation.Command.Text);
        var properties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(mutation.Command.Parameters["properties"]);
        Assert.Equal(person.Id.ToString("D"), properties["appId"]);
        Assert.Equal(person.Name, properties["name"]);
        Assert.Equal(31L, properties["age"]);
        Assert.Equal(1L, properties["version"]);
        Assert.Equal(0L, person.Version);
        Assert.Equal("Version", Assert.Single(mutation.GeneratedValues).Property.Name);
        Assert.Null(mutation.LockCommand);
    }

    [Fact]
    public void Modified_entry_preserves_only_modified_fields_and_null_removal_after_key_lock()
    {
        using var context = Context();
        var person = new Person { Id = Guid.NewGuid(), Name = "original", Age = 20, Version = 4 };
        context.Attach(person);
        person.Name = null;
        context.ChangeTracker.DetectChanges();
        var mutation = Assert.Single(Plan(context, person));
        Assert.Equal("MATCH (n:`MutationPerson` {`appId`: $k0})\nSET n.`__graphbridge_lock` = true REMOVE n.`__graphbridge_lock`\nRETURN count(n) AS count", mutation.LockCommand!.Text);
        Assert.Contains("WHERE coalesce(n.`version` = $o0", mutation.Command.Text);
        Assert.Contains("SET n += $changes", mutation.Command.Text);
        Assert.Equal(4L, mutation.Command.Parameters["o0"]);
        var changes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(mutation.Command.Parameters["changes"]);
        Assert.Equal(2, changes.Count);
        Assert.Null(changes["name"]);
        Assert.Equal(5L, changes["version"]);
        Assert.DoesNotContain("age", changes.Keys);
        Assert.DoesNotContain("appId", changes.Keys);
        Assert.Equal(4L, person.Version);
    }

    [Fact]
    public void Delete_matches_original_key_and_token_and_never_detaches_relationships()
    {
        using var context = Context();
        var person = new Person { Id = Guid.NewGuid(), Version = 7 };
        context.Remove(person);
        var mutation = Assert.Single(Plan(context, person));
        Assert.Contains("DELETE n", mutation.Command.Text);
        Assert.DoesNotContain("DETACH", mutation.Command.Text);
        Assert.Equal(person.Id.ToString("D"), mutation.Command.Parameters["k0"]);
        Assert.Equal(7L, mutation.Command.Parameters["o0"]);
        Assert.NotNull(mutation.LockCommand);
        Assert.Empty(mutation.GeneratedValues);
    }

    [Fact]
    public void Nullable_caller_managed_concurrency_token_matches_null_original_and_writes_current_value()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<TokenPerson>().ToGraphNode("Tokens").Property(p => p.Token).IsConcurrencyToken();
        });
        var person = new TokenPerson { Id = Guid.NewGuid() };
        context.Attach(person);
        person.Token = "new-token";
        context.ChangeTracker.DetectChanges();
        var mutation = Assert.Single(Plan(context, person));
        Assert.Null(mutation.Command.Parameters["o0"]);
        Assert.Contains("IS NULL AND $o0 IS NULL", mutation.Command.Text);
        var changes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(mutation.Command.Parameters["changes"]);
        Assert.Equal("new-token", changes["Token"]);
        Assert.Empty(mutation.GeneratedValues);
    }

    [Fact]
    public void Composite_keys_parameterize_each_slot_in_declared_key_order()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            var entity = b.Entity<Composite>().ToGraphNode("Composite");
            entity.HasKey(p => new { p.Tenant, p.Code });
            entity.Property(p => p.Tenant).HasGraphPropertyName("tenant");
            entity.Property(p => p.Code).HasGraphPropertyName("code");
        });
        var entity = new Composite { Tenant = "tenant-value", Code = "code-value", Name = "a" };
        context.Attach(entity);
        entity.Name = "b";
        context.ChangeTracker.DetectChanges();
        var mutation = Assert.Single(Plan(context, entity));
        Assert.Contains("{`tenant`: $k0, `code`: $k1}", mutation.Command.Text);
        Assert.Equal("tenant-value", mutation.Command.Parameters["k0"]);
        Assert.Equal("code-value", mutation.Command.Parameters["k1"]);
    }

    [Fact]
    public void Unchanged_detached_entries_produce_no_commands_and_batch_order_is_preserved()
    {
        using var context = Context();
        var unchanged = new Person { Id = Guid.NewGuid() };
        context.Attach(unchanged);
        Assert.Empty(Plan(context, unchanged));
        context.Entry(unchanged).State = EntityState.Detached;
        Assert.Empty(Plan(context, unchanged));
        var first = new Person { Name = "first" };
        var second = new Person { Name = "second" };
        context.AddRange(first, second);
        var plan = Plan(context, first, second);
        Assert.Same(first, plan[0].Entry.ToEntityEntry().Entity);
        Assert.Same(second, plan[1].Entry.ToEntityEntry().Entity);
    }

    [Fact]
    public void Numeric_version_overflow_and_temporary_keys_fail_before_driver_execution()
    {
        using var context = Context();
        var person = new Person { Id = Guid.NewGuid(), Name = "old", Version = long.MaxValue };
        context.Attach(person);
        person.Name = "new";
        context.ChangeTracker.DetectChanges();
        Assert.Throws<OverflowException>(() => Plan(context, person));
        using var temporary = new ProviderModelContext(ProviderTestDriver.Create(), b =>
            b.Entity<IntegerKey>().ToGraphNode("Integers").Property(p => p.Id).HasValueGenerator<TemporaryKeyGenerator>());
        var integer = new IntegerKey();
        temporary.Add(integer);
        Assert.True(temporary.Entry(integer).Property(p => p.Id).IsTemporary);
        Assert.Contains("temporary", Assert.Throws<InvalidOperationException>(() => Plan(temporary, integer)).Message);
    }

    [Fact]
    public void Unsupported_store_generated_type_fails_before_driver_execution()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<TokenPerson>().ToGraphNode("Tokens").Property(p => p.Token).IsRowVersion();
        });
        var person = new TokenPerson();
        context.Add(person);
        Assert.Contains("long IsRowVersion", Assert.Throws<NotSupportedException>(() => Plan(context, person)).Message);
    }

    [Fact]
    public void Mutation_renderer_rejects_unsafe_metadata_and_null_or_missing_keys()
    {
        Assert.Throws<ArgumentException>(() => CypherMutationRenderer.Create("Bad`Label", new Dictionary<string, object?>()));
        Assert.Throws<ArgumentException>(() => CypherMutationRenderer.Create("Label", new Dictionary<string, object?> { ["bad`key"] = 1 }));
        Assert.Throws<InvalidOperationException>(() => CypherMutationRenderer.Lock("Label", new Dictionary<string, object?>()));
        Assert.Throws<InvalidOperationException>(() => CypherMutationRenderer.Delete("Label", new Dictionary<string, object?> { ["Id"] = null }, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Scoped_transaction_interface_and_query_coordinator_share_instance()
    {
        using var context = Context();
        Assert.Same(context.GetService<IDbContextTransactionManager>(), context.GetService<Neo4jTransactionManager>());
        Assert.Throws<InvalidOperationException>(() => context.Database.CommitTransactionAsync().GetAwaiter().GetResult());
        Assert.Throws<NotSupportedException>(() => context.Database.BeginTransaction());
    }

    private static ProviderModelContext Context() => new(ProviderTestDriver.Create(), b =>
    {
        var person = b.Entity<Person>().ToGraphNode("MutationPerson");
        person.Property(p => p.Id).HasGraphPropertyName("appId");
        person.Property(p => p.Name).HasGraphPropertyName("name");
        person.Property(p => p.Age).HasGraphPropertyName("age");
        person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
    });
    private static IReadOnlyList<GraphNodeMutation> Plan(DbContext context, params object[] entities)
        => new GraphNodeMutationPlanner(context.GetService<GraphBindingCompiler>()).Plan(entities.Select(entity => UpdateEntry(context, entity)).ToList());

    // Test-only pinned EF seam: actual tracked entries, rather than a fake tracker or fabricated update snapshots.
#pragma warning disable EF1001
    private static IUpdateEntry UpdateEntry(DbContext context, object entity) => context.Entry(entity).GetInfrastructure();
#pragma warning restore EF1001

    public sealed class Person
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public int Age { get; set; }
        public long Version { get; set; }
    }
    public sealed class TokenPerson { public Guid Id { get; set; } public string? Token { get; set; } }
    public sealed class Composite { public string Tenant { get; set; } = ""; public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
    public sealed class IntegerKey { public int Id { get; set; } }
    public sealed class TemporaryKeyGenerator : ValueGenerator<int>
    {
        public override bool GeneratesTemporaryValues => true;
        public override int Next(EntityEntry entry) => -1;
    }
}
