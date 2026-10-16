using Microsoft.EntityFrameworkCore.Query.Translations.Operators;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class ComparisonOperatorTranslationsNeo4jTest : ComparisonOperatorTranslationsTestBase<BasicTypesQueryNeo4jFixture>
{
    public ComparisonOperatorTranslationsNeo4jTest(BasicTypesQueryNeo4jFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    public override async Task Equal()
    {
        await base.Equal();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory, "coalesce(n.`Int` = $p0, (n.`Int` IS NULL AND $p0 IS NULL))");
    }

    public override async Task NotEqual()
    {
        await base.NotEqual();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory, "(NOT coalesce(n.`Int` = $p0, (n.`Int` IS NULL AND $p0 IS NULL)))");
    }

    public override async Task GreaterThan()
    {
        await base.GreaterThan();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory, "coalesce(n.`Int` > $p0, false)");
    }

    public override async Task GreaterThanOrEqual()
    {
        await base.GreaterThanOrEqual();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory, "coalesce(n.`Int` >= $p0, false)");
    }

    public override async Task LessThan()
    {
        await base.LessThan();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory, "coalesce(n.`Int` < $p0, false)");
    }

    public override async Task LessThanOrEqual()
    {
        await base.LessThanOrEqual();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory, "coalesce(n.`Int` <= $p0, false)");
    }
}
