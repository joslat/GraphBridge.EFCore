using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderQueryTests
{
    [Neo4jIntegrationFact]
    public async Task Contains_preserves_empty_null_duplicate_and_negated_membership_on_missing_properties()
    {
        var runId = NewRunId();
        var people = new[] { Person(null, 1), Person("first", 2), Person("quote' MATCH (victim) //", 3) };
        try
        {
            await SeedAsync(runId, people);
            await using var db = CreateContext();
            string?[] values = [];
            var query = db.People.AsNoTracking().Where(p => p.RunId == runId && values.Contains(p.Name));
            var text = query.ToQueryString();
            Assert.Contains("n.`name` IN $p1", text);
            Assert.DoesNotContain("victim", text);
            foreach (var selected in new string?[][] { [], [null], ["first"], ["first", null, "first"], [people[2].Name] })
            {
                values = selected;
                var expected = people.Where(p => values.Contains(p.Name)).Select(p => p.Id).OrderBy(id => id).ToArray();
                Assert.Equal(expected, (await query.ToListAsync()).Select(p => p.Id).OrderBy(id => id));
                Assert.Equal(people.Where(p => !values.Contains(p.Name)).Select(p => p.Id).OrderBy(id => id),
                    (await db.People.AsNoTracking().Where(p => p.RunId == runId && !values.Contains(p.Name)).ToListAsync()).Select(p => p.Id).OrderBy(id => id));
                Assert.Equal(text, query.ToQueryString());
            }
            Assert.Empty(db.ChangeTracker.Entries());
            output.WriteLine(text);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Contains_converts_Guid_enum_integer_and_nullable_bool_collections_and_accepts_bound()
    {
        var runId = NewRunId();
        var first = Person("first", 1); first.State = PersonState.Active; first.Active = true;
        var second = Person("second", 2); second.OptionalState = PersonState.Pending;
        try
        {
            await SeedAsync(runId, first, second);
            await using var db = CreateContext();
            var query = db.People.AsNoTracking().Where(p => p.RunId == runId);
            Guid[] ids = [first.Id];
            Assert.Equal(first.Id, Assert.Single(await query.Where(p => ids.Contains(p.Id)).ToListAsync()).Id);
            var states = new List<PersonState> { PersonState.Active };
            Assert.Equal(first.Id, Assert.Single(await query.Where(p => states.Contains(p.State)).ToListAsync()).Id);
            PersonState?[] optional = [null];
            Assert.Equal(first.Id, Assert.Single(await query.Where(p => optional.Contains(p.OptionalState)).ToListAsync()).Id);
            bool?[] flags = [null];
            Assert.Equal(second.Id, Assert.Single(await query.Where(p => flags.Contains(p.Active)).ToListAsync()).Id);
            var numbers = Enumerable.Range(1, 1024).ToArray();
            Assert.Equal(2, (await query.Where(p => numbers.Contains(p.Age)).ToListAsync()).Count);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Scalar_queryable_ContainsAsync_executes_server_equality_including_null_and_rejects_paging()
    {
        var runId = NewRunId();
        var person = Person(null, 17);
        try
        {
            await SeedAsync(runId, person);
            await using var db = CreateContext();
            var query = db.People.Where(p => p.RunId == runId);
            Assert.True(await query.Select(p => p.Id).ContainsAsync(person.Id));
            Assert.False(await query.Select(p => p.Id).ContainsAsync(Guid.NewGuid()));
            Assert.True(await query.Select(p => p.Name).ContainsAsync(null));
            Assert.False(await query.Select(p => p.Name).ContainsAsync("missing"));
            Assert.True(await query.Select(p => p.Age).ContainsAsync(17));
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => query.Take(1).Select(p => p.Id).ContainsAsync(person.Id));
            await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => query.ContainsAsync(person));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Changing_membership_values_and_length_reuses_one_EF_async_compilation()
    {
        var runId = NewRunId();
        var people = new[] { Person("first", 1), Person("second", 2), Person("third", 3) };
        var compilations = 0;
        try
        {
            await SeedAsync(runId, people);
            await using var db = new QueryContext(new DbContextOptionsBuilder<QueryContext>()
                .UseNeo4j(fixture.Driver, fixture.Database)
                .LogTo((id, _) => id == CoreEventId.QueryCompilationStarting, _ => compilations++).Options);
            var cacheMembershipValues = new[] { 1 };
            var query = db.People.AsNoTracking().Where(p => p.RunId == runId && cacheMembershipValues.Contains(p.Age));
            Assert.Equal(people[0].Id, Assert.Single(await query.ToListAsync()).Id);
            Assert.Equal(1, compilations);
            cacheMembershipValues = [2, 3, 3];
            Assert.Equal(2, (await query.ToListAsync()).Count);
            cacheMembershipValues = [];
            Assert.Empty(await query.ToListAsync());
            Assert.Equal(1, compilations);
        }
        finally { await CleanupAsync(runId); }
    }
}
