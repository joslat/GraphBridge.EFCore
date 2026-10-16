using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>Actual EF DbSet query execution; the driver is used only to seed and inspect the disposable graph.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderQueryTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Where_uses_server_parameters_and_reuses_shape_with_changed_closure_values()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("below", 912), Person("boundary", 913), Person("above", 914));
            await using var context = CreateContext();
            var minimum = 913;
            var query = context.People.Where(p => p.RunId == runId && p.Age >= minimum);
            var cypher = query.ToQueryString();
            output.WriteLine(cypher);
            Assert.Contains("MATCH", cypher, StringComparison.Ordinal);
            Assert.Contains("WHERE", cypher, StringComparison.Ordinal);
            Assert.Contains("$", cypher, StringComparison.Ordinal);
            Assert.Contains("`age` >=", cypher, StringComparison.Ordinal);
            Assert.DoesNotContain(runId, cypher, StringComparison.Ordinal);
            Assert.DoesNotContain("913", cypher, StringComparison.Ordinal);
            Assert.DoesNotContain("914", cypher, StringComparison.Ordinal);

            var first = await query.ToListAsync();
            Assert.Equal(new[] { "above", "boundary" }, first.Select(p => p.Name).OrderBy(name => name));
            minimum = 914;
            var second = await query.ToListAsync();
            Assert.Equal("above", Assert.Single(second).Name);
            Assert.Equal(cypher, query.ToQueryString());

            await using var fresh = CreateContext();
            var fromFresh = await fresh.People.Where(p => p.RunId == runId && p.Age >= minimum).ToListAsync();
            Assert.Equal(Assert.Single(second).Id, Assert.Single(fromFresh).Id);
            Assert.NotSame(second[0], fromFresh[0]);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Tracked_query_uses_EF_identity_and_preserves_original_values()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("original", 30));
            await using var context = CreateContext();
            var first = Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(EntityState.Unchanged, context.Entry(first).State);
            Assert.Equal("original", context.Entry(first).Property(p => p.Name).OriginalValue);
            first.Name = "local edit";
            context.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, context.Entry(first).State);

            var repeated = Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Same(first, repeated);
            Assert.Equal("local edit", repeated.Name);
            Assert.Equal("original", context.Entry(repeated).Property(p => p.Name).OriginalValue);
            Assert.Single(context.ChangeTracker.Entries<QueryPerson>());

            await using var fresh = CreateContext();
            var persisted = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal("original", persisted.Name);
            Assert.NotSame(first, persisted);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task No_tracking_materializes_distinct_references_and_leaves_EF_tracker_empty()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("untracked", 30));
            await using var context = CreateContext();
            var first = Assert.Single(await context.People.AsNoTracking().Where(p => p.RunId == runId).ToListAsync());
            var second = Assert.Single(await context.People.AsNoTracking().Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(first.Id, second.Id);
            Assert.NotSame(first, second);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Identity_resolution_deduplicates_returned_keys_without_tracking_context_entries()
    {
        var runId = NewRunId();
        var sameKey = Person("duplicate key", 30);
        try
        {
            // Deliberately no uniqueness constraint: two graph nodes return one EF key.
            await SeedAsync(runId, sameKey, sameKey);
            await using var context = CreateContext();
            var resolved = await context.People.AsNoTrackingWithIdentityResolution().Where(p => p.RunId == runId).ToListAsync();
            Assert.Equal(2, resolved.Count);
            Assert.Same(resolved[0], resolved[1]);
            Assert.Empty(context.ChangeTracker.Entries());

            var untracked = await context.People.AsNoTracking().Where(p => p.RunId == runId).ToListAsync();
            Assert.Equal(2, untracked.Count);
            Assert.NotSame(untracked[0], untracked[1]);
            var tracked = await context.People.Where(p => p.RunId == runId).ToListAsync();
            Assert.Equal(2, tracked.Count);
            Assert.Same(tracked[0], tracked[1]);
            Assert.Single(context.ChangeTracker.Entries<QueryPerson>());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Nullable_scalars_Guid_enum_and_shadow_values_materialize_through_EF()
    {
        var runId = NewRunId();
        var populated = Person("O'Reilly `$value` \\ parameter", 31);
        populated.Active = true;
        populated.Score = 3.25;
        populated.State = PersonState.Active;
        populated.OptionalState = PersonState.Active;
        populated.OptionalNumber = 57;
        var missing = Person(null, 32);
        try
        {
            await SeedAsync(runId, populated, missing);
            await using var context = CreateContext();
            var records = await context.People.Where(p => p.RunId == runId).ToListAsync();
            Assert.Equal(2, records.Count);
            var actual = records.Single(p => p.Id == populated.Id);
            Assert.Equal(populated.Name, actual.Name);
            Assert.Equal(populated.Id, actual.Id);
            Assert.True(actual.Active);
            Assert.Equal(3.25, actual.Score);
            Assert.Equal(PersonState.Active, actual.State);
            Assert.Equal(PersonState.Active, actual.OptionalState);
            Assert.Equal(57, actual.OptionalNumber);
            Assert.Equal("shadow-" + populated.Id.ToString("N"), context.Entry(actual).Property<string>("ExternalCode").CurrentValue);
            var nulls = records.Single(p => p.Id == missing.Id);
            Assert.Null(nulls.Name);
            Assert.Null(nulls.Active);
            Assert.Null(nulls.Score);
            Assert.Null(nulls.OptionalState);
            Assert.Null(nulls.OptionalNumber);
            Assert.Equal(PersonState.Pending, nulls.State);

            var activeState = PersonState.Active;
            var byConvertedParameters = await context.People.Where(p => p.RunId == runId && p.Id == populated.Id && p.State == activeState).ToListAsync();
            Assert.Same(actual, Assert.Single(byConvertedParameters));
            var shadowCode = "shadow-" + populated.Id.ToString("N");
            var byShadow = await context.People.Where(p => p.RunId == runId && EF.Property<string>(p, "ExternalCode") == shadowCode).ToListAsync();
            Assert.Same(actual, Assert.Single(byShadow));
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Pre_cancelled_query_fails_and_context_remains_usable()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("after cancellation", 30));
            await using var context = CreateContext();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => context.People.Where(p => p.RunId == runId).ToListAsync(cancellation.Token));
            Assert.Equal("after cancellation", Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync()).Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Early_disposed_async_enumerator_releases_session_for_following_queries()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("first", 30), Person("second", 31));
            await using var context = CreateContext();
            await using (var enumerator = context.People.Where(p => p.RunId == runId).AsAsyncEnumerable().GetAsyncEnumerator())
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal(runId, enumerator.Current.RunId);
            }

            var complete = await context.People.Where(p => p.RunId == runId).ToListAsync();
            Assert.Equal(2, complete.Count);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Unsupported_predicate_fails_without_invoking_client_code()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("would match on client", 30));
            await using var context = CreateContext();
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.People
                .Where(p => p.RunId == runId && ClientOnlyPredicate(p.Name)).ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Synchronous_entity_query_is_explicitly_rejected()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("async required", 30));
            await using var context = CreateContext();
            var exception = Assert.Throws<NotSupportedException>(() => context.People.Where(p => p.RunId == runId).ToList());
            Assert.Contains("async", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Nullable_ordering_thenby_and_parameterized_paging_match_LINQ_results()
    {
        var runId = NewRunId();
        var seeds = new[] { Person("same", 40), Person(null, 50), Person("z", 10), Person("same", 20), Person("a", 30) };
        try
        {
            await SeedAsync(runId, seeds);
            await using var context = CreateContext();
            var ascending = await context.People.AsNoTracking().Where(p => p.RunId == runId)
                .OrderBy(p => p.Name).ThenBy(p => p.Age).ThenBy(p => p.Id).ToListAsync();
            Assert.Equal(seeds.OrderBy(p => p.Name).ThenBy(p => p.Age).ThenBy(p => p.Id).Select(p => p.Id),
                ascending.Select(p => p.Id));
            Assert.Null(ascending[0].Name);

            var descending = await context.People.AsNoTracking().Where(p => p.RunId == runId)
                .OrderByDescending(p => p.Name).ThenBy(p => p.Age).ThenBy(p => p.Id).ToListAsync();
            Assert.Equal(seeds.OrderByDescending(p => p.Name).ThenBy(p => p.Age).ThenBy(p => p.Id).Select(p => p.Id),
                descending.Select(p => p.Id));
            Assert.Null(descending[^1].Name);

            var offset = 1;
            var pageSize = 3;
            IQueryable<QueryPerson> PageQuery() => context.People.AsNoTracking().Where(p => p.RunId == runId)
                .OrderBy(p => p.Name).ThenBy(p => p.Age).ThenBy(p => p.Id).Skip(offset).Take(pageSize);
            var pageQuery = PageQuery();
            var cypher = pageQuery.ToQueryString();
            Assert.Contains("ORDER BY", cypher, StringComparison.Ordinal);
            Assert.Contains("SKIP $", cypher, StringComparison.Ordinal);
            Assert.Contains("LIMIT $", cypher, StringComparison.Ordinal);
            Assert.Equal(seeds.OrderBy(p => p.Name).ThenBy(p => p.Age).ThenBy(p => p.Id).Skip(offset).Take(pageSize).Select(p => p.Id),
                (await pageQuery.ToListAsync()).Select(p => p.Id));
            offset = 0;
            pageSize = 0;
            // Skip/Take receive integers when the IQueryable is constructed; rebuild that same shape.
            var emptyPage = PageQuery();
            Assert.Equal(cypher, emptyPage.ToQueryString());
            Assert.Empty(await emptyPage.ToListAsync());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task First_and_single_cardinality_distinguish_empty_one_and_multiple_rows()
    {
        var runId = NewRunId();
        var lower = Person("lower", 20);
        var higher = Person("higher", 30);
        try
        {
            await SeedAsync(runId, lower, higher);
            await using var context = CreateContext();
            var scoped = context.People.Where(p => p.RunId == runId);
            Assert.Null(await scoped.Where(p => p.Age < 0).FirstOrDefaultAsync());
            Assert.Null(await scoped.Where(p => p.Age < 0).SingleOrDefaultAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.Where(p => p.Age < 0).FirstAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.Where(p => p.Age < 0).SingleAsync());

            var single = await scoped.Where(p => p.Id == lower.Id).SingleOrDefaultAsync();
            Assert.NotNull(single);
            Assert.Equal(lower.Id, single.Id);
            Assert.Same(single, await scoped.Where(p => p.Id == lower.Id).FirstOrDefaultAsync());
            Assert.Same(single, await scoped.OrderBy(p => p.Age).FirstOrDefaultAsync());
            Assert.Equal(higher.Id, (await scoped.OrderByDescending(p => p.Age).FirstAsync()).Id);
            Assert.Equal(lower.Id, (await scoped.OrderBy(p => p.Age).Take(1).SingleAsync()).Id);
            Assert.Equal(higher.Id, (await scoped.OrderBy(p => p.Age).Skip(1).Take(1).SingleOrDefaultAsync())!.Id);
            Assert.Null(await scoped.Take(0).FirstOrDefaultAsync());
            Assert.Null(await scoped.Take(0).SingleOrDefaultAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.SingleOrDefaultAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.SingleAsync());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Unsupported_operator_compositions_fail_instead_of_reordering_server_operations()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("lower", 20), Person("higher", 30));
            await using var context = CreateContext();
            var scoped = context.People.Where(p => p.RunId == runId);
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(
                () => scoped.OrderBy(p => p.Age).Take(1).Where(p => p.Age >= 30).ToListAsync());
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(
                () => scoped.Take(1).OrderBy(p => p.Age).ToListAsync());
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => scoped.Take(1).CountAsync());
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => scoped.Take(1).AnyAsync());
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => scoped.Take(2).Take(1).ToListAsync());
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => scoped.Take(2).Skip(1).ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Corrupt_required_and_converted_store_values_fail_and_following_query_remains_usable()
    {
        var corruptions = new[]
        {
            (Property: "Id", Value: (object?)null, Exception: typeof(InvalidOperationException)),
            (Property: "age", Value: (object?)null, Exception: typeof(InvalidOperationException)),
            (Property: "age", Value: (object?)((long)int.MaxValue + 1), Exception: typeof(OverflowException)),
            (Property: "Id", Value: (object?)"not-a-guid", Exception: typeof(FormatException))
        };
        foreach (var corruption in corruptions)
        {
            var runId = NewRunId();
            try
            {
                var valid = Person("valid", 30);
                await SeedAsync(runId, valid);
                var invalidProperties = new Dictionary<string, object?>
                {
                    ["Id"] = Guid.NewGuid().ToString("D"), ["RunId"] = runId,
                    ["name"] = "corrupt", ["age"] = 31L, ["state"] = 0L
                };
                invalidProperties[corruption.Property] = corruption.Value;
                await SeedPropertiesAsync([invalidProperties]);
                await using var context = CreateContext();
                var exception = await Record.ExceptionAsync(() => context.People
                    .Where(p => p.RunId == runId && p.Name == "corrupt").ToListAsync());
                Assert.NotNull(exception);
                Assert.Equal(corruption.Exception, exception.GetType());
                if (corruption.Value is null)
                {
                    Assert.Contains("required property", exception.Message, StringComparison.OrdinalIgnoreCase);
                }
                Assert.Empty(context.ChangeTracker.Entries());

                var following = await context.People.Where(p => p.RunId == runId && p.Name == "valid").ToListAsync();
                Assert.Equal(valid.Id, Assert.Single(following).Id);
                Assert.Equal(EntityState.Unchanged, context.Entry(following[0]).State);
            }
            finally { await CleanupAsync(runId); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Context_disposed_before_async_enumeration_throws_and_borrowed_driver_survives()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("still stored", 30));
            var context = CreateContext();
            var query = context.People.Where(p => p.RunId == runId);
            await context.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => query.ToListAsync());
            await using var fresh = CreateContext();
            Assert.Equal("still stored", Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync()).Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Count_long_count_any_and_scalar_projections_execute_against_filtered_graph()
    {
        var runId = NewRunId();
        var first = Person(null, 20);
        first.State = PersonState.Active;
        var second = Person("second", 30);
        try
        {
            await SeedAsync(runId, first, second);
            await using var context = CreateContext();
            var scoped = context.People.Where(p => p.RunId == runId);
            Assert.Equal(2, await scoped.CountAsync());
            Assert.Equal(1, await scoped.CountAsync(p => p.Age >= 30));
            Assert.Equal(0, await scoped.Where(p => p.Age < 0).CountAsync());
            Assert.Equal(2L, await scoped.LongCountAsync());
            Assert.Equal(1L, await scoped.LongCountAsync(p => p.Age >= 30));
            Assert.Equal(0L, await scoped.Where(p => p.Age < 0).LongCountAsync());
            Assert.True(await scoped.AnyAsync());
            Assert.True(await scoped.AnyAsync(p => p.Age >= 30));
            Assert.False(await scoped.Where(p => p.Age < 0).AnyAsync());

            Assert.Equal(new string?[] { null, "second" }, await scoped.OrderBy(p => p.Age).Select(p => p.Name).ToListAsync());
            Assert.Equal(new[] { first.Id, second.Id }, await scoped.OrderBy(p => p.Age).Select(p => p.Id).ToListAsync());
            Assert.Equal(new[] { PersonState.Active, PersonState.Pending }, await scoped.OrderBy(p => p.Age).Select(p => p.State).ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Null_equality_inequality_and_boolean_predicates_match_CLR_semantics()
    {
        var runId = NewRunId();
        var missing = Person(null, 30);
        var active = Person("active", 31);
        active.Active = true;
        active.OptionalNumber = 57;
        var inactive = Person("inactive", 32);
        inactive.Active = false;
        inactive.OptionalNumber = 58;
        try
        {
            await SeedAsync(runId, missing, active, inactive);
            await using var context = CreateContext();
            var scoped = context.People.AsNoTracking().Where(p => p.RunId == runId);
            Assert.Equal(missing.Id, Assert.Single(await scoped.Where(p => p.Name == null).ToListAsync()).Id);
            Assert.Equal(2, (await scoped.Where(p => p.Name != null).ToListAsync()).Count);
            Assert.Equal(new[] { missing.Id, inactive.Id }.OrderBy(id => id),
                (await scoped.Where(p => p.Active != true).ToListAsync()).Select(p => p.Id).OrderBy(id => id));
            Assert.Equal(new[] { missing.Id, inactive.Id }.OrderBy(id => id),
                (await scoped.Where(p => p.OptionalNumber != 57).ToListAsync()).Select(p => p.Id).OrderBy(id => id));
            Assert.Equal(new[] { missing.Id, active.Id }.OrderBy(id => id),
                (await scoped.Where(p => !(p.Age < 20) && (p.Name == null || p.Active == true)).ToListAsync()).Select(p => p.Id).OrderBy(id => id));
        }
        finally { await CleanupAsync(runId); }
    }

    private QueryContext CreateContext() => new(new DbContextOptionsBuilder<QueryContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task SeedAsync(string runId, params QueryPerson[] people)
    {
        var properties = people.Select(person => new Dictionary<string, object?>
        {
            ["Id"] = person.Id.ToString("D"), ["RunId"] = runId,
            ["name"] = person.Name, ["age"] = person.Age,
            ["active"] = person.Active, ["score"] = person.Score,
            ["state"] = (long)person.State,
            ["optionalState"] = person.OptionalState is null ? null : (long)person.OptionalState,
            ["optionalNumber"] = person.OptionalNumber,
            ["externalCode"] = "shadow-" + person.Id.ToString("N")
        }).ToList();
        await SeedPropertiesAsync(properties);
    }

    private async Task SeedPropertiesAsync(IReadOnlyList<Dictionary<string, object?>> properties)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "UNWIND $properties AS properties CREATE (p:GraphBridgeQueryPerson) SET p = properties", new { properties });
        Assert.Equal(properties.Count, (await cursor.ConsumeAsync()).Counters.NodesCreated);
    }

    private async Task CleanupAsync(string runId)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (p:GraphBridgeQueryPerson {RunId: $runId}) DELETE p", new { runId });
        await cursor.ConsumeAsync();
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N");
    private static QueryPerson Person(string? name, int age) => new() { Id = Guid.NewGuid(), Name = name, Age = age };
    private static bool ClientOnlyPredicate(string? name) => throw new InvalidOperationException("Client predicate must never execute.");

    public enum PersonState { Pending = 0, Active = 2 }

    public sealed class QueryPerson
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string? Name { get; set; }
        public int Age { get; set; }
        public bool? Active { get; set; }
        public double? Score { get; set; }
        public PersonState State { get; set; }
        public PersonState? OptionalState { get; set; }
        public int? OptionalNumber { get; set; }
    }

    public sealed class QueryContext(DbContextOptions<QueryContext> options) : DbContext(options)
    {
        public DbSet<QueryPerson> People => Set<QueryPerson>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var person = modelBuilder.Entity<QueryPerson>();
            person.ToGraphNode("GraphBridgeQueryPerson");
            person.HasKey(p => p.Id);
            person.Property(p => p.Id).ValueGeneratedNever();
            person.Property(p => p.Name).HasGraphPropertyName("name");
            person.Property(p => p.Age).HasGraphPropertyName("age");
            person.Property(p => p.Active).HasGraphPropertyName("active");
            person.Property(p => p.Score).HasGraphPropertyName("score");
            person.Property(p => p.State).HasGraphPropertyName("state");
            person.Property(p => p.OptionalState).HasGraphPropertyName("optionalState");
            person.Property(p => p.OptionalNumber).HasGraphPropertyName("optionalNumber");
            person.Property<string>("ExternalCode").HasGraphPropertyName("externalCode");
        }
    }
}
