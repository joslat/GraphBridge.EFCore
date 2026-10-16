using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderQueryTests
{
    [Neo4jIntegrationFact]
    public async Task DTO_projection_matches_control_values_with_nested_null_converted_and_shadow_fields()
    {
        var runId = NewRunId();
        var first = Person(null, 20); first.State = PersonState.Active; first.OptionalNumber = 7;
        var second = Person("second", 30);
        var people = new[] { first, second };
        try
        {
            await SeedAsync(runId, people);
            await using var db = CreateContext();
            var root = db.People.Where(p => p.RunId == runId).OrderBy(p => p.Age);
            var records = root.Select(p => new PersonSummary(p.Name, p.Age));
            var cypher = records.ToQueryString();
            output.WriteLine(cypher);
            Assert.DoesNotContain("n.`Id` AS", cypher);
            Assert.Equal(people.Select(p => new PersonSummary(p.Name, p.Age)), await records.ToListAsync());
            var initializers = await root.Select(p => new PersonSummaryInitializer { Name = p.Name, Age = p.Age }).ToListAsync();
            Assert.Equal(people.Select(p => p.Name), initializers.Select(p => p.Name));
            Assert.Equal(people.Select(p => p.Age), initializers.Select(p => p.Age));
            var nested = await root.Select(p => new
            {
                p.Name, Duplicate = p.Name,
                Details = new { p.Id, p.State, p.OptionalState, p.OptionalNumber, Age = (int?)p.Age },
                Shadow = EF.Property<string>(p, "ExternalCode")
            }).ToListAsync();
            for (var i = 0; i < people.Length; i++)
            {
                Assert.Equal(people[i].Name, nested[i].Name);
                Assert.Equal(nested[i].Name, nested[i].Duplicate);
                Assert.Equal(people[i].Id, nested[i].Details.Id);
                Assert.Equal(people[i].State, nested[i].Details.State);
                Assert.Equal(people[i].OptionalState, nested[i].Details.OptionalState);
                Assert.Equal(people[i].OptionalNumber, nested[i].Details.OptionalNumber);
                Assert.Equal(people[i].Age, nested[i].Details.Age);
                Assert.Equal("shadow-" + people[i].Id.ToString("N"), nested[i].Shadow);
            }
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task DTO_captured_parameters_rebind_without_leaking_into_Cypher_or_cached_results()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("row", 20));
            await using var db = CreateContext();
            var text = "first-private-value";
            var query = db.People.Where(p => p.RunId == runId).Select(p => new { p.Age, Text = text });
            var constant = db.People.Where(p => p.RunId == runId).Select(p => new { Text = text });
            var dto = new PersonSummary(text, 1);
            var captured = db.People.Where(p => p.RunId == runId).Select(p => dto);
            var cypher = query.ToQueryString();
            foreach (var value in new[] { "first-private-value", "second-private-value" })
            {
                text = value;
                dto = new PersonSummary(value, 2);
                Assert.Equal(value, Assert.Single(await query.ToListAsync()).Text);
                Assert.Equal(value, Assert.Single(await constant.ToListAsync()).Text);
                Assert.Same(dto, Assert.Single(await captured.ToListAsync()));
                Assert.Equal(cypher, query.ToQueryString());
                Assert.DoesNotContain(value, cypher);
                Assert.DoesNotContain(value, constant.ToQueryString());
            }
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Empty_and_constant_DTO_projections_preserve_row_count_paging_and_empty_results()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("a", 1), Person("b", 2), Person("c", 3));
            await using var db = CreateContext();
            var root = db.People.Where(p => p.RunId == runId);
            Assert.Equal(3, (await root.Select(p => new { }).ToListAsync()).Count);
            Assert.Equal(new[] { 10, 10, 10 }, (await root.Select(p => new { X = 10 }).ToListAsync()).Select(p => p.X));
            Assert.Equal(new[] { true, true }, await root.OrderBy(p => p.Age).Skip(1).Take(2).Select(p => true).ToListAsync());
            Assert.Empty(await root.Where(p => p.Age < 0).Select(p => new { }).ToListAsync());
            Assert.Equal(3, await root.Select(p => new { p.Name }).CountAsync());
            Assert.True(await root.Select(p => new { p.Name }).AnyAsync());
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Entity_inside_DTO_uses_EF_tracking_and_no_tracking_identity_resolution()
    {
        var runId = NewRunId();
        var person = Person("duplicate", 20);
        try
        {
            await SeedAsync(runId, person, person);
            await using var db = CreateContext();
            var root = db.People.Where(p => p.RunId == runId);
            var tracked = await root.Select(p => new { p.Name, Entity = p }).ToListAsync();
            Assert.Equal(2, tracked.Count);
            Assert.Same(tracked[0].Entity, tracked[1].Entity);
            Assert.Single(db.ChangeTracker.Entries());
            db.ChangeTracker.Clear();
            var untracked = await root.AsNoTracking().Select(p => new { Entity = p }).ToListAsync();
            Assert.NotSame(untracked[0].Entity, untracked[1].Entity);
            var resolved = await root.AsNoTrackingWithIdentityResolution().Select(p => new { Nested = new { Entity = p } }).ToListAsync();
            Assert.Same(resolved[0].Nested.Entity, resolved[1].Nested.Entity);
            Assert.Equal(person.Id, resolved[0].Nested.Entity.Id);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task DTO_constructor_failure_preserves_error_and_allows_subsequent_query()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("failure", 1));
            await using var db = CreateContext();
            var query = db.People.Where(p => p.RunId == runId);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(p => new FailingSummary(p.Name)).ToListAsync());
            Assert.Equal("DTO construction failed.", error.Message);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Equal("failure", Assert.Single(await query.Select(p => new PersonSummary(p.Name, p.Age)).ToListAsync()).Name);
        }
        finally { await CleanupAsync(runId); }
    }

    public sealed record PersonSummary(string? Name, int Age);
    public sealed class PersonSummaryInitializer { public string? Name { get; set; } public int Age { get; set; } }
    public sealed class FailingSummary
    {
        public FailingSummary(string? name) => throw new InvalidOperationException("DTO construction failed.");
    }
}
