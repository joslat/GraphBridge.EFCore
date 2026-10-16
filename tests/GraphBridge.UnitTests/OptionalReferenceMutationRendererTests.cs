using GraphBridge.EntityFrameworkCore.Cypher;

namespace GraphBridge.UnitTests;

public sealed class OptionalReferenceMutationRendererTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Optional_transition_checks_original_membership_before_conditional_mutation(bool hadParent, bool hasParent)
    {
        var command = CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent",
            hadParent ? Key("old-value") : null, hasParent ? Key("new-value") : null, "Child", Key("dependent-value"));
        var text = command.Text;
        Assert.StartsWith("MATCH (d:`Child` {`Id`: $d0})\nCALL {\n    WITH d\n    OPTIONAL MATCH (parent)-[r:`HAS_CHILD`]->(d)", text);
        Assert.Contains("WITH parent,r LIMIT 2\n    RETURN collect(r) AS links,collect(parent) AS parents", text);
        Assert.DoesNotContain("OPTIONAL MATCH (parent:", text);
        Assert.DoesNotContain("DISTINCT", text);
        Assert.DoesNotContain("DETACH", text);
        Assert.EndsWith("RETURN size(eligible) AS count", text);
        Assert.Contains(hadParent ? "CASE WHEN size(links) = 1 AND parents[0] = p" : "CASE WHEN size(links) = 0", text);
        Assert.Equal(hadParent, text.Contains("MATCH (p:`Parent` {`Id`: $p0})", StringComparison.Ordinal));
        Assert.Equal(hasParent, text.Contains("MATCH (q:`Parent` {`Id`: $q0})", StringComparison.Ordinal));
        Assert.Equal(hadParent, text.Contains("FOREACH (r IN CASE WHEN size(eligible) = 1 THEN links ELSE [] END | DELETE r)", StringComparison.Ordinal));
        Assert.Equal(hasParent, text.Contains("FOREACH (ignored IN eligible | CREATE (q)-[:`HAS_CHILD`]->(d))", StringComparison.Ordinal));
        if (hadParent) Assert.True(text.IndexOf("AS eligible", StringComparison.Ordinal) < text.IndexOf("DELETE r", StringComparison.Ordinal));
        if (hasParent) Assert.True(text.IndexOf("AS eligible", StringComparison.Ordinal) < text.IndexOf("CREATE (q)", StringComparison.Ordinal));
        Assert.Equal(1 + (hadParent ? 1 : 0) + (hasParent ? 1 : 0), command.Parameters.Count);
        Assert.Equal("dependent-value", command.Parameters["d0"]);
        if (hadParent) Assert.Equal("old-value", command.Parameters["p0"]);
        if (hasParent) Assert.Equal("new-value", command.Parameters["q0"]);
        Assert.DoesNotContain("value", text);
    }

    [Fact]
    public void Present_composite_keys_are_parameterized_in_declared_order_without_alias_collisions()
    {
        var oldKeys = new Dictionary<string, object?> { ["Tenant"] = 17L, ["Code"] = "old' MATCH (x) //" };
        var newKeys = new Dictionary<string, object?> { ["Tenant"] = 18L, ["Code"] = "new' MATCH (x) //" };
        var command = CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent", oldKeys, newKeys, "Child", Key("child"));
        Assert.Contains("MATCH (p:`Parent` {`Tenant`: $p0, `Code`: $p1})", command.Text);
        Assert.Contains("MATCH (q:`Parent` {`Tenant`: $q0, `Code`: $q1})", command.Text);
        Assert.Equal(17L, command.Parameters["p0"]);
        Assert.Equal(18L, command.Parameters["q0"]);
        Assert.Equal(oldKeys["Code"], command.Parameters["p1"]);
        Assert.Equal(newKeys["Code"], command.Parameters["q1"]);
        Assert.DoesNotContain("MATCH (x)", command.Text);
    }

    [Fact]
    public void Absence_is_not_an_empty_or_partial_endpoint_key()
    {
        foreach (var malformed in new[] { new Dictionary<string, object?>(), Key(null), new Dictionary<string, object?> { ["Tenant"] = 1L, ["Code"] = null } })
        {
            Assert.Throws<InvalidOperationException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent", malformed, null, "Child", Key("child")));
            Assert.Throws<InvalidOperationException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent", null, malformed, "Child", Key("child")));
        }
        Assert.Throws<InvalidOperationException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent", null, null, "Child", Key(null)));
    }

    [Fact]
    public void Metadata_validation_also_applies_when_both_principals_are_absent()
    {
        Assert.Throws<ArgumentException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("bad`type", "Parent", null, null, "Child", Key("child")));
        Assert.Throws<ArgumentException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "bad`label", null, null, "Child", Key("child")));
        Assert.Throws<ArgumentException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent", null, null, "bad`label", Key("child")));
        Assert.Throws<ArgumentException>(() => CypherNodeReferenceMutationRenderer.TransitionOptional("HAS_CHILD", "Parent", null, null, "Child", new Dictionary<string, object?> { ["bad`key"] = "child" }));
    }

    private static Dictionary<string, object?> Key(object? value) => new() { ["Id"] = value };
}
