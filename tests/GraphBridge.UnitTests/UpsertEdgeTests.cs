using GraphBridge.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using static GraphBridge.UnitTests.UpsertTests;

namespace GraphBridge.UnitTests;

// ADR 0051, section 6. The scripted protocol checks the client side; the live suite establishes MERGE and locking on the server.
public sealed class UpsertEdgeTests
{
    [Fact]
    public async Task An_identified_edge_is_preflighted_then_merged_on_its_key_between_converted_endpoints()
    {
        var script = new Script { RelationshipsCreated = 1 };
        await using var db = Edges(script);
        var (person, shelf) = (Guid.NewGuid(), Guid.NewGuid());
        var result = await db.Set<UpsertVisit>().ExecuteUpsertAsync([new UpsertVisit { Id = 7, PersonId = person, ShelfId = shelf },
            new UpsertVisit { Id = 8, PersonId = person, ShelfId = shelf }]);

        Assert.Equal(new GraphUpsertResult(1, 1, new GraphWriteCounters(0, 0, 1, 0, 0, 0, 0)), result);
        Assert.Equal(3, script.Commands.Count);
        Assert.StartsWith("SHOW CONSTRAINTS", script.Commands[0]);
        var preflight = script.Commands[1];
        Assert.Contains("OPTIONAL MATCH (s:`UpsertPerson` {`person_key`: row.source.`person_key`})", preflight);
        Assert.Contains("FOREACH (x IN CASE WHEN s IS NULL THEN [] ELSE [s] END | SET x.`__graphbridge_lock` = true REMOVE x.`__graphbridge_lock`)", preflight);
        Assert.DoesNotContain("SET x.`person_key`", preflight);
        Assert.Contains("OPTIONAL MATCH (a)-[e:`UPSERT_VISITED` {`visit_key`: row.key.`visit_key`}]->(b) WHERE s IS NULL OR t IS NULL OR a <> s OR b <> t", preflight);
        Assert.EndsWith("RETURN row.position AS position, s IS NOT NULL AS source, t IS NOT NULL AS target, count(e) AS edges", preflight);
        Assert.Equal("UNWIND $__gb_upsert_rows AS row\nMATCH (s:`UpsertPerson` {`person_key`: row.source.`person_key`})\n"
            + "MATCH (t:`UpsertShelf` {`Id`: row.target.`Id`})\nMERGE (s)-[r:`UPSERT_VISITED` {`visit_key`: row.key.`visit_key`}]->(t)\n"
            + "ON CREATE SET r += row.create\nRETURN count(r) AS affected", script.Commands[2]);
        var row = ((Dictionary<string, object?>[])script.Parameters[2]![Neo4jUpsertExecutor.RowsParameter])[1];
        Assert.Equal(1L, row["position"]);
        Assert.Equal(person.ToString("D"), ((Dictionary<string, object?>)row["source"]!)["person_key"]);
        Assert.Equal(shelf.ToString("D"), ((Dictionary<string, object?>)row["target"]!)["Id"]);
        Assert.Equal(8L, ((Dictionary<string, object?>)row["key"]!)["visit_key"]);
        Assert.Equal((1, 0), (script.Commits, script.Rollbacks));
    }

    [Fact]
    public async Task An_association_merges_on_its_endpoint_pair_with_its_payload()
    {
        var script = new Script();
        await using var db = Edges(script);
        var rating = new UpsertRating { PersonId = Guid.NewGuid(), ShelfId = Guid.NewGuid(), Stars = 4 };
        await db.Set<UpsertRating>().ExecuteUpsertAsync([rating]);
        Assert.Equal("UNWIND $__gb_upsert_rows AS row\nMATCH (s:`UpsertPerson` {`person_key`: row.source.`person_key`})\n"
            + "MATCH (t:`UpsertShelf` {`Id`: row.target.`Id`})\nMERGE (s)-[r:`UPSERT_RATED`]->(t)\n"
            + "ON CREATE SET r += row.create\nON MATCH SET r += row.update\nRETURN count(r) AS affected", script.Commands[2]);
        Assert.Contains("OPTIONAL MATCH (s)-[e:`UPSERT_RATED`]->(t) WHERE s IS NOT NULL AND t IS NOT NULL", script.Commands[1]);
        var row = ((Dictionary<string, object?>[])script.Parameters[2]![Neo4jUpsertExecutor.RowsParameter])[0];
        Assert.Equal(4L, ((Dictionary<string, object?>)row["create"]!)["Stars"]);
        Assert.Equal(4L, ((Dictionary<string, object?>)row["update"]!)["Stars"]);
        Assert.False(row.ContainsKey("key"));

        script.Commands.Clear(); script.Parameters.Clear();
        await db.Set<UpsertRating>().ExecuteUpsertAsync([rating], r => new { });
        Assert.DoesNotContain("ON MATCH", script.Commands[2]);
    }

    [Theory]
    [InlineData("missing", "name an endpoint node that doesn't exist")]
    [InlineData("rebound", "already belongs to a relationship between other endpoints")]
    public async Task The_preflight_refuses_the_whole_call_with_positions_and_writes_nothing(string problem, string message)
    {
        var script = new Script
        {
            Preflight = i => new Dictionary<string, object?>
            {
                ["position"] = (long)i, ["source"] = !(problem == "missing" && i == 1), ["target"] = true,
                ["edges"] = problem == "rebound" && i == 2 ? 1L : 0L
            }
        };
        await using var db = Edges(script);
        var person = Guid.NewGuid();
        var visits = Enumerable.Range(1, 3).Select(i => new UpsertVisit { Id = i, PersonId = person, ShelfId = Guid.NewGuid() }).ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<UpsertVisit>().ExecuteUpsertAsync(visits));
        Assert.Contains(message, error.Message);
        Assert.Contains(problem == "missing" ? "Entities 1 (zero-based)" : "Entities 2 (zero-based)", error.Message);
        Assert.DoesNotContain(person.ToString(), error.Message);
        Assert.Equal(2, script.Commands.Count);
        Assert.Equal(0, script.Commits);
    }

    [Fact]
    public async Task An_association_pair_with_two_relationships_is_refused()
    {
        var script = new Script { Preflight = i => new() { ["position"] = (long)i, ["source"] = true, ["target"] = true, ["edges"] = 2L } };
        await using var db = Edges(script);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<UpsertRating>()
            .ExecuteUpsertAsync([new UpsertRating { PersonId = Guid.NewGuid(), ShelfId = Guid.NewGuid() }]));
        Assert.Contains("more than one such relationship", error.Message);
        Assert.Equal(0, script.Commits);
    }

    [Fact]
    public async Task Duplicates_sentinel_keys_selectors_on_keys_and_shadow_payloads_are_refused_before_anything_is_sent()
    {
        var script = new Script();
        await using var db = Edges(script);
        var (person, shelf) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Contains("link the same endpoints", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<UpsertRating>().ExecuteUpsertAsync(
            [new UpsertRating { PersonId = person, ShelfId = shelf }, new UpsertRating { PersonId = person, ShelfId = shelf, Stars = 1 }]))).Message);
        Assert.Contains("Entities 0 and 1", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<UpsertVisit>().ExecuteUpsertAsync(
            [new UpsertVisit { Id = 3, PersonId = person, ShelfId = shelf }, new UpsertVisit { Id = 3, PersonId = Guid.NewGuid(), ShelfId = shelf }]))).Message);
        Assert.Contains("client-supplied keys", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<UpsertVisit>().ExecuteUpsertAsync(
            [new UpsertVisit { PersonId = person, ShelfId = shelf }]))).Message);
        Assert.Contains("names an endpoint", (await Assert.ThrowsAsync<ArgumentException>(() => db.Set<UpsertRating>().ExecuteUpsertAsync(
            [new UpsertRating { PersonId = person, ShelfId = shelf }], r => r.PersonId))).Message);
        Assert.Contains("identifies the relationship", (await Assert.ThrowsAsync<ArgumentException>(() => db.Set<UpsertVisit>().ExecuteUpsertAsync(
            [new UpsertVisit { Id = 1, PersonId = person, ShelfId = shelf }], v => v.Id))).Message);
        Assert.Empty(script.Commands);

        await using var shadow = Edges(script, b => b.Entity<UpsertRating>().Property<int>("Weight"));
        Assert.Contains("shadow property", (await Assert.ThrowsAsync<NotSupportedException>(() => shadow.Set<UpsertRating>().ExecuteUpsertAsync(
            [new UpsertRating { PersonId = person, ShelfId = shelf }]))).Message);
        Assert.Empty(script.Commands);
    }

    [Fact]
    public async Task Concurrency_tokens_and_a_missing_relationship_constraint_are_refused_before_any_write()
    {
        var script = new Script { RelationshipConstrained = false };
        var (person, shelf) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var db = Edges(script))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<UpsertVisit>().ExecuteUpsertAsync(
                [new UpsertVisit { Id = 1, PersonId = person, ShelfId = shelf }]));
            Assert.Contains("relationship uniqueness constraint for :UPSERT_VISITED (visit_key)", error.Message);
            // Only the schema check ran: no preflight, no MERGE.
            Assert.Single(script.Commands);
            Assert.StartsWith("SHOW CONSTRAINTS", script.Commands[0], StringComparison.Ordinal);
        }

        await using var token = Edges(script, b => b.Entity<UpsertRating>().Property(r => r.Stars).IsConcurrencyToken());
        Assert.Contains("concurrency tokens", (await Assert.ThrowsAsync<NotSupportedException>(() => token.Set<UpsertRating>().ExecuteUpsertAsync(
            [new UpsertRating { PersonId = person, ShelfId = shelf }]))).Message);
        Assert.Single(script.Commands);
    }

    private static ProviderModelContext Edges(Script script, Action<ModelBuilder>? extra = null) => new(script.Driver, b =>
    {
        var person = b.Entity<UpsertPerson>().ToGraphNode("UpsertPerson");
        person.Property(p => p.Id).HasGraphPropertyName("person_key").ValueGeneratedNever();
        person.Property(p => p.Mood).HasConversion<string>();
        b.Entity<UpsertShelf>().ToGraphNode("UpsertShelf").Property(s => s.Id).ValueGeneratedNever();
        var rating = b.Entity<UpsertRating>();
        rating.HasKey(r => new { r.PersonId, r.ShelfId });
        rating.HasOne<UpsertPerson>().WithMany().HasForeignKey(r => r.PersonId);
        rating.HasOne<UpsertShelf>().WithMany().HasForeignKey(r => r.ShelfId);
        rating.ToGraphRelationship("UPSERT_RATED", [nameof(UpsertRating.PersonId)], [nameof(UpsertRating.ShelfId)]);
        var visit = b.Entity<UpsertVisit>();
        visit.Property(v => v.Id).HasGraphPropertyName("visit_key").ValueGeneratedNever();
        visit.HasOne<UpsertPerson>().WithMany().HasForeignKey(v => v.PersonId);
        visit.HasOne<UpsertShelf>().WithMany().HasForeignKey(v => v.ShelfId);
        visit.ToGraphIdentifiedRelationship("UPSERT_VISITED", nameof(UpsertVisit.Id), [nameof(UpsertVisit.PersonId)], [nameof(UpsertVisit.ShelfId)]);
        extra?.Invoke(b);
    });
}
