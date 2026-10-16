using GraphBridge.EntityFrameworkCore.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>Pure rendering checks. Neo4j syntax, row counts and graph effects require live integration.</summary>
public sealed class AssociationRendererTests
{
    [Fact]
    public void Merge_binds_directed_endpoints_and_sets_payload_only_on_create_before_guarded_idempotency_check()
    {
        var command = CypherAssociationMutationRenderer.Merge("ACTED_IN", "Person", Key("actorId", "actor"),
            "Movie", Key("movieId", "film"), Values(("Role", "lead"), ("Note", null)));

        Assert.Equal("""
            MATCH (s:`Person` {`actorId`: $s0})
            MATCH (t:`Movie` {`movieId`: $t0})
            MERGE (s)-[r:`ACTED_IN`]->(t)
            ON CREATE SET r = $properties
            WITH r LIMIT 2
            WITH collect(r) AS edges
            WITH CASE WHEN size(edges) = 1 AND coalesce(edges[0].`Role` = $p0, (edges[0].`Role` IS NULL AND $p0 IS NULL)) AND coalesce(edges[0].`Note` = $p1, (edges[0].`Note` IS NULL AND $p1 IS NULL)) THEN edges ELSE [] END AS eligible
            RETURN size(eligible) AS count
            """, command.Text);
        Assert.Equal("actor", command.Parameters["s0"]);
        Assert.Equal("film", command.Parameters["t0"]);
        Assert.Equal("lead", command.Parameters["p0"]);
        Assert.Null(command.Parameters["p1"]);
        Assert.DoesNotContain("ON MATCH", command.Text);
        Assert.DoesNotContain("CREATE (s", command.Text);
        Assert.DoesNotContain("CREATE (t", command.Text);
    }

    [Fact]
    public void Update_guards_all_physical_candidates_before_tokens_and_changes_only_eligible_edge()
    {
        var command = CypherAssociationMutationRenderer.Update("WORKS_FOR", "Person", Key("id", "person"),
            "Organization", Key("id", "organization"), Values(("Token", null)), Values(("Role", "architect"), ("Note", null)));

        Assert.Equal("""
            MATCH (s:`Person` {`id`: $s0})
            MATCH (t:`Organization` {`id`: $t0})
            MATCH (s)-[r:`WORKS_FOR`]->(t)
            WITH r LIMIT 2
            WITH collect(r) AS edges
            WITH CASE WHEN size(edges) = 1 THEN edges ELSE [] END AS edges
            FOREACH (r IN edges | SET r.`Token` = r.`Token`)
            WITH CASE WHEN size(edges) = 1 AND coalesce(edges[0].`Token` = $o0, (edges[0].`Token` IS NULL AND $o0 IS NULL)) THEN edges ELSE [] END AS eligible
            FOREACH (r IN eligible | SET r += $changes)
            RETURN size(eligible) AS count
            """, command.Text);
        Assert.Null(command.Parameters["o0"]);
        var changes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(command.Parameters["changes"]);
        Assert.Equal("architect", changes["Role"]);
        Assert.Null(changes["Note"]);
        Assert.Equal(2, changes.Count);
        Assert.DoesNotContain("WHERE", command.Text);
        Assert.DoesNotContain("MERGE", command.Text);
        Assert.DoesNotContain("SET r =", command.Text);
    }

    [Fact]
    public void Delete_guards_duplicates_then_original_version_and_deletes_only_relationship()
    {
        var command = CypherAssociationMutationRenderer.Delete("ACTED_IN", "Person", Key("id", 1L),
            "Movie", Key("id", 2L), Values(("Version", 7L)));

        Assert.Equal("""
            MATCH (s:`Person` {`id`: $s0})
            MATCH (t:`Movie` {`id`: $t0})
            MATCH (s)-[r:`ACTED_IN`]->(t)
            WITH r LIMIT 2
            WITH collect(r) AS edges
            WITH CASE WHEN size(edges) = 1 THEN edges ELSE [] END AS edges
            FOREACH (r IN edges | SET r.`Version` = r.`Version`)
            WITH CASE WHEN size(edges) = 1 AND coalesce(edges[0].`Version` = $o0, (edges[0].`Version` IS NULL AND $o0 IS NULL)) THEN edges ELSE [] END AS eligible
            FOREACH (r IN eligible | DELETE r)
            RETURN size(eligible) AS count
            """, command.Text);
        Assert.Equal(7L, command.Parameters["o0"]);
        Assert.DoesNotContain("DETACH", command.Text);
        Assert.DoesNotContain("DELETE s", command.Text);
        Assert.DoesNotContain("DELETE t", command.Text);
        Assert.DoesNotContain("properties", command.Parameters.Keys);
    }

    [Fact]
    public void Composite_endpoint_store_slots_keep_declared_order_and_separate_parameter_names()
    {
        var command = CypherAssociationMutationRenderer.Merge("LINK", "Source",
            Values(("tenant", "source-tenant"), ("externalId", "source-code")), "Target",
            Values(("tenant", "target-tenant"), ("externalId", "target-code")), Values());

        Assert.StartsWith("MATCH (s:`Source` {`tenant`: $s0, `externalId`: $s1})\nMATCH (t:`Target` {`tenant`: $t0, `externalId`: $t1})", command.Text);
        Assert.Equal("source-tenant", command.Parameters["s0"]);
        Assert.Equal("source-code", command.Parameters["s1"]);
        Assert.Equal("target-tenant", command.Parameters["t0"]);
        Assert.Equal("target-code", command.Parameters["t1"]);
        Assert.Contains("WITH CASE WHEN size(edges) = 1 THEN edges ELSE [] END AS eligible", command.Text);
    }

    [Fact]
    public void Value_injection_stays_in_parameter_values_for_keys_payload_and_original_tokens()
    {
        const string injected = "O'Reilly`) MATCH (unrelated) DETACH DELETE unrelated //";
        var merge = CypherAssociationMutationRenderer.Merge("LINK", "Source", Key("id", injected),
            "Target", Key("id", injected), Values(("Role", injected)));
        var update = CypherAssociationMutationRenderer.Update("LINK", "Source", Key("id", injected),
            "Target", Key("id", injected), Values(("Token", injected)), Values(("Role", injected)));
        var delete = CypherAssociationMutationRenderer.Delete("LINK", "Source", Key("id", injected),
            "Target", Key("id", injected), Values(("Token", injected)));

        foreach (var command in new[] { merge, update, delete })
        {
            Assert.DoesNotContain(injected, command.Text);
            Assert.DoesNotContain("DETACH", command.Text);
            Assert.Equal(injected, command.Parameters["s0"]);
            Assert.Equal(injected, command.Parameters["t0"]);
        }
        Assert.Equal(injected, merge.Parameters["p0"]);
        Assert.Equal(injected, update.Parameters["o0"]);
        Assert.Equal(injected, delete.Parameters["o0"]);
    }

    [Theory]
    [InlineData("bad`name")]
    [InlineData("name) MATCH (n)")]
    [InlineData("9bad")]
    [InlineData("é")]
    [InlineData("")]
    public void Unsafe_names_in_every_metadata_role_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Merge(name, "Source", Key("id", 1), "Target", Key("id", 2), Values()));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Merge("LINK", name, Key("id", 1), "Target", Key("id", 2), Values()));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Merge("LINK", "Source", Key("id", 1), name, Key("id", 2), Values()));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Merge("LINK", "Source", Key(name, 1), "Target", Key("id", 2), Values()));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Merge("LINK", "Source", Key("id", 1), "Target", Key(name, 2), Values()));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Merge("LINK", "Source", Key("id", 1), "Target", Key("id", 2), Values((name, 3))));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Update("LINK", "Source", Key("id", 1), "Target", Key("id", 2), Values((name, 3)), Values()));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Update("LINK", "Source", Key("id", 1), "Target", Key("id", 2), Values(), Values((name, 3))));
        Assert.Throws<ArgumentException>(() => CypherAssociationMutationRenderer.Delete("LINK", "Source", Key("id", 1), "Target", Key("id", 2), Values((name, 3))));
    }

    [Fact]
    public void Each_endpoint_requires_nonempty_nonnull_application_key_slots()
    {
        Assert.Throws<InvalidOperationException>(() => CypherAssociationMutationRenderer.Merge("LINK", "Source", Values(), "Target", Key("id", 2), Values()));
        Assert.Throws<InvalidOperationException>(() => CypherAssociationMutationRenderer.Merge("LINK", "Source", Key("id", 1), "Target", Values(), Values()));
        Assert.Throws<InvalidOperationException>(() => CypherAssociationMutationRenderer.Update("LINK", "Source", Key("id", null), "Target", Key("id", 2), Values(), Values()));
        Assert.Throws<InvalidOperationException>(() => CypherAssociationMutationRenderer.Delete("LINK", "Source", Key("id", 1), "Target", Key("id", null), Values()));
    }

    [Fact]
    public void Self_association_direction_uses_distinct_bound_roles_with_equal_labels_and_keys()
    {
        var command = CypherAssociationMutationRenderer.Delete("KNOWS", "Person", Key("id", "same"),
            "Person", Key("id", "same"), Values());

        Assert.Contains("MATCH (s:`Person` {`id`: $s0})", command.Text);
        Assert.Contains("MATCH (t:`Person` {`id`: $t0})", command.Text);
        Assert.Contains("MATCH (s)-[r:`KNOWS`]->(t)", command.Text);
        Assert.DoesNotContain("<-[", command.Text);
    }

    [Fact]
    public void No_token_mutations_need_no_relationship_token_self_assignment()
    {
        var update = CypherAssociationMutationRenderer.Update("LINK", "Source", Key("id", 1L),
            "Target", Key("id", 2L), Values(), Values(("Role", "new")));
        var delete = CypherAssociationMutationRenderer.Delete("LINK", "Source", Key("id", 1L),
            "Target", Key("id", 2L), Values());

        Assert.DoesNotContain("FOREACH (r IN edges", update.Text);
        Assert.DoesNotContain("FOREACH (r IN edges", delete.Text);
        Assert.Contains("WITH CASE WHEN size(edges) = 1 THEN edges ELSE [] END AS eligible", update.Text);
        Assert.Contains("WITH CASE WHEN size(edges) = 1 THEN edges ELSE [] END AS eligible", delete.Text);
    }

    [Fact]
    public void First_original_property_locks_singleton_before_all_originals_are_compared()
    {
        var command = CypherAssociationMutationRenderer.Update("LINK", "Source", Key("id", 1L),
            "Target", Key("id", 2L), Values(("Version", 4L), ("Token", "before")), Values(("Version", 5L)));

        const string locking = "FOREACH (r IN edges | SET r.`Version` = r.`Version`)";
        Assert.Contains(locking, command.Text);
        Assert.DoesNotContain("SET r.`Token` = r.`Token`", command.Text);
        Assert.True(command.Text.IndexOf(locking, StringComparison.Ordinal) < command.Text.IndexOf("$o0", StringComparison.Ordinal));
        Assert.True(command.Text.IndexOf(locking, StringComparison.Ordinal) < command.Text.IndexOf("$o1", StringComparison.Ordinal));
        Assert.Equal(4L, command.Parameters["o0"]);
        Assert.Equal("before", command.Parameters["o1"]);
        Assert.Contains("FOREACH (r IN eligible | SET r += $changes)", command.Text);
    }

    private static IReadOnlyDictionary<string, object?> Key(string name, object? value) => Values((name, value));
    private static IReadOnlyDictionary<string, object?> Values(params (string Name, object? Value)[] values)
        => values.ToDictionary(pair => pair.Name, pair => pair.Value);
}
