using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderQueryTests
{
    [Neo4jIntegrationFact]
    public async Task FindAsync_reuses_compiled_shape_for_changed_keys_and_preserves_EF_tracking()
    {
        var runId = NewRunId();
        var first = Person("find-first", 11);
        var second = Person("find-second", 12);
        try
        {
            await SeedAsync(runId, first, second);
            var compilations = 0;
            await using var context = new QueryContext(new DbContextOptionsBuilder<QueryContext>()
                .UseNeo4j(fixture.Driver, fixture.Database)
                .LogTo((id, _) => id == CoreEventId.QueryCompilationStarting, _ => compilations++).Options);
            var loaded = await context.People.FindAsync(first.Id);
            Assert.NotNull(loaded);
            Assert.Equal(first.Name, loaded.Name);
            var afterFirst = compilations; // The shared cache may already contain this shape.
            loaded.Name = "local edit";
            Assert.Same(loaded, await context.People.FindAsync(first.Id));
            Assert.Equal("local edit", loaded.Name);
            Assert.Equal(second.Name, (await context.People.FindAsync(second.Id))!.Name);
            context.ChangeTracker.Clear();
            Assert.Equal(first.Name, (await context.People.FindAsync(first.Id))!.Name);
            Assert.Null(await context.People.FindAsync(Guid.NewGuid()));
            Assert.Null(await context.People.FindAsync(new object?[] { null }));
            Assert.Equal(afterFirst, compilations);
            Assert.Single(context.ChangeTracker.Entries<QueryPerson>());
            await Assert.ThrowsAsync<ArgumentException>(() => context.People.FindAsync("wrong-key-type").AsTask());
            await using var fresh = CreateContext();
            Assert.Equal(second.Name, (await fresh.People.FindAsync(second.Id))!.Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Guid_object_Equals_filters_and_negation_match_live_values()
    {
        var runId = NewRunId();
        var first = Person("equals-first", 11);
        var second = Person("equals-second", 12);
        try
        {
            await SeedAsync(runId, first, second);
            await using var context = CreateContext();
            var key = first.Id;
            var query = context.People.AsNoTracking().Where(p => p.RunId == runId && object.Equals(p.Id, key));
            var cypher = query.ToQueryString();
            output.WriteLine(cypher);
            Assert.DoesNotContain(key.ToString(), cypher);
            Assert.Equal(first.Id, Assert.Single(await query.ToListAsync()).Id);
            key = second.Id;
            Assert.Equal(second.Id, Assert.Single(await query.ToListAsync()).Id);
            Assert.Equal(cypher, query.ToQueryString());
            Assert.Equal(first.Id, Assert.Single(await context.People.AsNoTracking()
                .Where(p => p.RunId == runId && !object.Equals(p.Id, key)).ToListAsync()).Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Instance_Equals_filters_match_live_values()
    {
        var runId = NewRunId();
        var first = Person("instance-equals-first", 11);
        var second = Person("instance-equals-second", 12);
        second.State = PersonState.Active;
        try
        {
            await SeedAsync(runId, first, second);
            await using var context = CreateContext();
            var key = first.Id;
            var name = second.Name;
            var query = context.People.AsNoTracking().Where(p => p.RunId == runId && p.Id.Equals(key));
            var cypher = query.ToQueryString();
            output.WriteLine(cypher);
            Assert.DoesNotContain(key.ToString(), cypher);
            Assert.Equal(first.Id, Assert.Single(await query.ToListAsync()).Id);
            Assert.Equal(second.Id, Assert.Single(await context.People.AsNoTracking()
                .Where(p => p.RunId == runId && p.Name!.Equals(name)).ToListAsync()).Id);
            Assert.Equal(second.Id, Assert.Single(await context.People.AsNoTracking()
                .Where(p => p.RunId == runId && !p.Id.Equals(key)).ToListAsync()).Id);
            Assert.Equal(second.Id, Assert.Single(await context.People.AsNoTracking()
                .Where(p => p.RunId == runId && p.State.Equals(PersonState.Active) && p.Age.Equals(12)).ToListAsync()).Id);
        }
        finally { await CleanupAsync(runId); }
    }
}
