using GraphBridge.EntityFrameworkCore.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>Pure diagnostic rendering checks; no pinned-server syntax, counts or execution are proven here.</summary>
public sealed class AssociationReadGuardRendererTests
{
    [Fact]
    public void Full_projection_counts_exact_bound_directed_pair_before_payload_filter_and_paging()
    {
        var plan = new CypherQueryPlan(new CypherAssociationRoot("Person", "ACTED_IN", "Movie"),
            [new("personId", "c0", CypherPropertyOwner.Source), new("movieId", "c1", CypherPropertyOwner.Target),
                new("role", "c2", CypherPropertyOwner.Relationship)],
            new CypherBinary(new CypherProperty("role", CypherPropertyOwner.Relationship), CypherOperator.Equal,
                new CypherParameter("p0")), [new("role", true, CypherPropertyOwner.Relationship)], "p1", "p2");

        var text = CypherQueryRenderer.Render(plan);

        Assert.Equal("""
            MATCH (s:`Person`)-[r:`ACTED_IN`]->(t:`Movie`)
            CALL (s, t) {
              MATCH (s)-[candidate:`ACTED_IN`]->(t)
              WITH candidate LIMIT 2
              RETURN count(*) AS `__gb_association_count`
            }
            WITH s, r, t, `__gb_association_count`
            WHERE coalesce(r.`role` = $p0, (r.`role` IS NULL AND $p0 IS NULL))
            RETURN s.`personId` AS `c0`, t.`movieId` AS `c1`, r.`role` AS `c2`, `__gb_association_count` AS `__gb_association_count`
            ORDER BY r.`role` IS NULL DESC, r.`role` ASC
            SKIP $p1
            LIMIT $p2
            """, text);
        var subquery = text[text.IndexOf("CALL (", StringComparison.Ordinal)..text.IndexOf("\n}", StringComparison.Ordinal)];
        Assert.DoesNotContain("WHERE", subquery);
        Assert.DoesNotContain("role", subquery);
        Assert.DoesNotContain("$p", subquery);
        Assert.DoesNotContain("DISTINCT", text);
        Assert.DoesNotContain("collect(", text);
        Assert.DoesNotContain("__gb_association_count` = 1", text);
    }

    [Theory]
    [InlineData((int)CypherPropertyOwner.Source)]
    [InlineData((int)CypherPropertyOwner.Relationship)]
    [InlineData((int)CypherPropertyOwner.Target)]
    public void Scalar_projection_retains_diagnostic_when_neither_endpoint_identity_is_projected(int owner)
    {
        var text = CypherQueryRenderer.Render(new(new CypherAssociationRoot("Source", "LINK", "Target"),
            [new("name", "scalar", (CypherPropertyOwner)owner)]));
        var alias = (CypherPropertyOwner)owner switch
        {
            CypherPropertyOwner.Source => "s",
            CypherPropertyOwner.Relationship => "r",
            CypherPropertyOwner.Target => "t",
            _ => throw new InvalidOperationException()
        };

        Assert.EndsWith($"RETURN {alias}.`name` AS `scalar`, `__gb_association_count` AS `__gb_association_count`", text);
        Assert.Contains("MATCH (s)-[candidate:`LINK`]->(t)\n  WITH candidate LIMIT 2", text);
    }

    [Fact]
    public void Self_association_counts_source_to_target_and_keeps_outer_relationship_rows()
    {
        var text = CypherQueryRenderer.Render(new(new CypherAssociationRoot("Person", "KNOWS", "Person"),
            [new("id", "c0", CypherPropertyOwner.Source)]));

        Assert.StartsWith("MATCH (s:`Person`)-[r:`KNOWS`]->(t:`Person`)", text);
        Assert.Contains("CALL (s, t) {\n  MATCH (s)-[candidate:`KNOWS`]->(t)", text);
        Assert.Contains("WITH s, r, t, `__gb_association_count`", text);
        Assert.DoesNotContain("<-[", text);
        Assert.DoesNotContain("DISTINCT", text);
    }

    [Fact]
    public void Reserved_diagnostic_column_alias_rejects_collision_but_equal_store_name_remains_valid()
    {
        var root = new CypherAssociationRoot("Source", "LINK", "Target");
        var error = Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root,
            [new("payload", CypherQueryPlan.AssociationCountAlias, CypherPropertyOwner.Relationship)])));
        Assert.Contains("reserved", error.Message);

        var text = CypherQueryRenderer.Render(new(root,
            [new(CypherQueryPlan.AssociationCountAlias, "payload", CypherPropertyOwner.Relationship)]));
        Assert.EndsWith("RETURN r.`__gb_association_count` AS `payload`, `__gb_association_count` AS `__gb_association_count`", text);
    }

    [Fact]
    public void Duplicate_association_column_aliases_are_rejected_before_decoder_ambiguity()
    {
        var plan = new CypherQueryPlan(new CypherAssociationRoot("Source", "LINK", "Target"),
            [new("sourceId", "same", CypherPropertyOwner.Source), new("targetId", "same", CypherPropertyOwner.Target)]);
        Assert.Contains("distinct output aliases", Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan)).Message);
    }

    [Theory]
    [InlineData((int)CypherAggregate.Count)]
    [InlineData((int)CypherAggregate.Any)]
    public void Association_aggregate_cannot_discard_the_per_edge_diagnostic(int aggregate)
    {
        var plan = new CypherQueryPlan(new CypherAssociationRoot("Source", "LINK", "Target"),
            [new("payload", "c0", CypherPropertyOwner.Relationship)], Aggregate: (CypherAggregate)aggregate);
        Assert.Contains("cardinality diagnostic", Assert.Throws<NotSupportedException>(() => CypherQueryRenderer.Render(plan)).Message);
    }

    [Fact]
    public void Node_predicate_projection_ordering_and_paging_keep_exact_existing_output()
    {
        var plan = new CypherQueryPlan("Person", [new("name", "c0")],
            new CypherBinary(new CypherProperty("name"), CypherOperator.Equal, new CypherParameter("p0")),
            [new("name", false)], "p1", "p2");

        Assert.Equal("MATCH (n:`Person`)\nWHERE coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL))\n"
            + "RETURN n.`name` AS `c0`\nORDER BY n.`name` IS NULL ASC, n.`name` DESC\nSKIP $p1\nLIMIT $p2",
            CypherQueryRenderer.Render(plan));
        Assert.DoesNotContain(CypherQueryPlan.AssociationCountAlias, CypherQueryRenderer.Render(plan));
        Assert.Equal("MATCH (n:`Person`)\nRETURN count(*) AS aggregate",
            CypherQueryRenderer.Render(new("Person", [new("name", "c0")], Aggregate: CypherAggregate.Count)));
    }

    [Fact]
    public void Node_only_plan_keeps_previous_alias_behavior_without_an_association_diagnostic()
        => Assert.Equal("MATCH (n:`Person`)\nRETURN n.`value` AS `__gb_association_count`",
            CypherQueryRenderer.Render(new("Person", [new("value", CypherQueryPlan.AssociationCountAlias)])));

    [Fact]
    public void Guard_only_projection_is_valid_without_a_leading_comma()
    {
        var text = CypherQueryRenderer.Render(new(new CypherAssociationRoot("Source", "LINK", "Target"), []));
        Assert.EndsWith("RETURN `__gb_association_count` AS `__gb_association_count`", text);
        Assert.DoesNotContain("RETURN ,", text);
    }
}
