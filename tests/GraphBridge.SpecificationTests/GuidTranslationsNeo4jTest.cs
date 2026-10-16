using Microsoft.EntityFrameworkCore.Query.Translations;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class GuidTranslationsNeo4jTest : IClassFixture<BasicTypesQueryNeo4jFixture>
{
    private readonly BasicTypesQueryNeo4jFixture _fixture;
    private readonly UpstreamCases _upstream;

    public GuidTranslationsNeo4jTest(BasicTypesQueryNeo4jFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _upstream = new(fixture);
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    [ConditionalFact]
    public async Task New_with_constant()
    {
        await _upstream.New_with_constant();
        AssertBaseline();
    }

    [ConditionalFact]
    public async Task New_with_parameter()
    {
        await _upstream.New_with_parameter();
        AssertBaseline();
    }

    private void AssertBaseline() => CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
        "coalesce(n.`Guid` = $p0, (n.`Guid` IS NULL AND $p0 IS NULL))");

    private sealed class UpstreamCases(BasicTypesQueryNeo4jFixture fixture)
        : GuidTranslationsTestBase<BasicTypesQueryNeo4jFixture>(fixture);
}
