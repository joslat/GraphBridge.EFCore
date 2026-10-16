using Microsoft.EntityFrameworkCore.Query.Translations.Operators;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class LogicalOperatorTranslationsNeo4jTest : LogicalOperatorTranslationsTestBase<BasicTypesQueryNeo4jFixture>
{
    public LogicalOperatorTranslationsNeo4jTest(BasicTypesQueryNeo4jFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    public override async Task And()
    {
        await base.And();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory,
            "(coalesce(n.`Int` = $p0, (n.`Int` IS NULL AND $p0 IS NULL)) AND coalesce(n.`String` = $p1, (n.`String` IS NULL AND $p1 IS NULL)))");
    }

    public override async Task And_with_bool_property()
    {
        await base.And_with_bool_property();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory,
            "(coalesce(n.`Bool` = $p0, (n.`Bool` IS NULL AND $p0 IS NULL)) AND coalesce(n.`String` = $p1, (n.`String` IS NULL AND $p1 IS NULL)))");
    }

    public override async Task Or()
    {
        await base.Or();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory,
            "(coalesce(n.`Int` = $p0, (n.`Int` IS NULL AND $p0 IS NULL)) OR coalesce(n.`String` = $p1, (n.`String` IS NULL AND $p1 IS NULL)))");
    }

    public override async Task Or_with_bool_property()
    {
        await base.Or_with_bool_property();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory,
            "(coalesce(n.`Bool` = $p0, (n.`Bool` IS NULL AND $p0 IS NULL)) OR coalesce(n.`String` = $p1, (n.`String` IS NULL AND $p1 IS NULL)))");
    }

    public override async Task Not()
    {
        await base.Not();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory,
            "(NOT coalesce(coalesce(n.`Int` = $p0, (n.`Int` IS NULL AND $p0 IS NULL)), false))");
    }

    public override async Task Not_with_bool_property()
    {
        await base.Not_with_bool_property();
        CypherBaseline.AssertCommand(Fixture.ListLoggerFactory,
            "(NOT coalesce(coalesce(n.`Bool` = $p0, (n.`Bool` IS NULL AND $p0 IS NULL)), false))");
    }
}
