using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.UnitTests;

public sealed class GraphSchemaCapabilitiesTests
{
    [Theory]
    [InlineData("2026.09.0", "community", "25", false)]
    [InlineData("2026.09.0", "enterprise", "25", true)]
    [InlineData("2026.06.0", "Enterprise", "25", true)]
    [InlineData("2026.05.0", "enterprise", "25", false)]
    [InlineData("2026.09.0", "enterprise", "5", false)]
    [InlineData("2025.12.0", "enterprise", "25", false)]
    [InlineData("2027.01.0", "enterprise", "25", false)]
    [InlineData("2026.09-preview", "enterprise", "25", false)]
    public void Graph_type_observation_requires_known_GA_version_edition_and_explicitly_available_25(
        string version, string edition, string dialect, bool supported)
    {
        var facts = GraphSchemaCapabilities.Detect(version, edition, [dialect]);
        Assert.Equal(supported, facts.CanShowCurrentGraphType);
        Assert.Equal(version, facts.ServerVersion);
        Assert.Equal(edition, facts.Edition);
    }

    [Fact]
    public void Community_baseline_distinguishes_uniqueness_from_enterprise_existence_and_type_constraints()
    {
        var facts = GraphSchemaCapabilities.Detect("2026.09.0", "community", ["5", "25"]);
        Assert.Equal(GraphSchemaCapabilitySupport.Supported, facts.NodeUniqueness);
        Assert.Equal(GraphSchemaCapabilitySupport.Supported, facts.RelationshipUniqueness);
        Assert.Equal(GraphSchemaCapabilitySupport.Supported, facts.RangeIndexes);
        Assert.Equal(GraphSchemaCapabilitySupport.Unsupported, facts.PropertyExistence);
        Assert.Equal(GraphSchemaCapabilitySupport.Unsupported, facts.PropertyType);
        Assert.Equal(GraphSchemaCapabilitySupport.Unsupported, facts.KeyConstraints);
        Assert.True(facts.HasPropertyTypeObservation);
    }

    [Theory]
    [InlineData("5.6.0", false, false)]
    [InlineData("5.7.0", true, false)]
    [InlineData("5.9.0", true, true)]
    public void Documented_constraint_version_boundaries_are_separate(string version, bool relationship, bool types)
    {
        var facts = GraphSchemaCapabilities.Detect(version, "enterprise", ["5"]);
        Assert.Equal(relationship ? GraphSchemaCapabilitySupport.Supported : GraphSchemaCapabilitySupport.Unsupported,
            facts.RelationshipUniqueness);
        Assert.Equal(types ? GraphSchemaCapabilitySupport.Supported : GraphSchemaCapabilitySupport.Unsupported, facts.PropertyType);
        Assert.Equal(types, facts.HasPropertyTypeObservation);
    }

    [Theory]
    [InlineData("not-a-version", "community")]
    [InlineData("2026.13.0", "enterprise")]
    [InlineData("2026.09.0", "unknown-edition")]
    [InlineData("4.4.0", "enterprise")]
    public void Unknown_environment_does_not_claim_DDL_capabilities(string version, string edition)
    {
        var facts = GraphSchemaCapabilities.Detect(version, edition, ["25"]);
        Assert.Equal(GraphSchemaCapabilitySupport.Unknown, facts.NodeUniqueness);
        Assert.Equal(GraphSchemaCapabilitySupport.Unknown, facts.PropertyExistence);
        Assert.False(facts.CanShowCurrentGraphType);
    }

    [Fact]
    public void Advertised_dialects_are_copied_and_do_not_identify_the_effective_default()
    {
        var versions = new List<string> { "5", "25", "25" };
        var facts = GraphSchemaCapabilities.Detect("2026.09.0", "community", versions);
        versions.Clear();
        Assert.Equal(new[] { "25", "5" }, facts.AdvertisedCypherVersions);
        Assert.False(facts.CanShowCurrentGraphType);
    }
}
