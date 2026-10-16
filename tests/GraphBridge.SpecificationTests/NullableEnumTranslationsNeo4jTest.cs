using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.Translations;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

// Explicit equality slice. Composition deliberately avoids discovering the upstream bitwise
// family as implicit supported scope; each call executes the unmodified packaged upstream method.
[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class NullableEnumTranslationsNeo4jTest : IClassFixture<BasicTypesQueryNeo4jFixture>
{
    private readonly BasicTypesQueryNeo4jFixture _fixture;
    private readonly UpstreamCases _upstream;

    public NullableEnumTranslationsNeo4jTest(BasicTypesQueryNeo4jFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _upstream = new UpstreamCases(fixture);
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    [ConditionalFact]
    public async Task Equality_to_constant()
    {
        await _upstream.Equality_to_constant();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))");
    }

    [ConditionalFact]
    public async Task Equality_to_parameter()
    {
        await _upstream.Equality_to_parameter();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))");
    }

    [ConditionalFact]
    public async Task Equality_nullable_enum_to_constant()
    {
        await _upstream.Equality_nullable_enum_to_constant();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))", nullable: true);
    }

    [ConditionalFact]
    public async Task Equality_nullable_enum_to_parameter()
    {
        await _upstream.Equality_nullable_enum_to_parameter();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))", nullable: true);
    }

    [ConditionalFact]
    public async Task Equality_nullable_enum_to_null_constant()
    {
        await _upstream.Equality_nullable_enum_to_null_constant();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))", nullable: true);
    }

    [ConditionalFact]
    public async Task Equality_nullable_enum_to_null_parameter()
    {
        await _upstream.Equality_nullable_enum_to_null_parameter();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))", nullable: true);
    }

    [ConditionalFact]
    public async Task Equality_nullable_enum_to_nullable_parameter()
    {
        await _upstream.Equality_nullable_enum_to_nullable_parameter();
        CypherBaseline.AssertCommand(_fixture.ListLoggerFactory,
            "coalesce(n.`Enum` = $p0, (n.`Enum` IS NULL AND $p0 IS NULL))", nullable: true);
    }

    private sealed class UpstreamCases(BasicTypesQueryNeo4jFixture fixture)
        : EnumTranslationsTestBase<BasicTypesQueryNeo4jFixture>(fixture);
}
