using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class NorthwindAggregatesNeo4jTest : IClassFixture<NorthwindAggregatesNeo4jFixture>
{
    private readonly Cases _cases;
    public NorthwindAggregatesNeo4jTest(NorthwindAggregatesNeo4jFixture fixture, ITestOutputHelper output)
    {
        _cases = new Cases(fixture);
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }
    [ConditionalFact] public Task Min_with_no_arg() => _cases.Min_with_no_arg(true);
    [ConditionalFact] public Task Min_with_arg() => _cases.Min_with_arg(true);
    [ConditionalFact] public Task Min_no_data() => _cases.Min_no_data(true);
    [ConditionalFact] public Task Min_no_data_cast_to_nullable() => _cases.Min_no_data_cast_to_nullable(true);
    [ConditionalFact] public Task Max_with_no_arg() => _cases.Max_with_no_arg(true);
    [ConditionalFact] public Task Max_with_arg() => _cases.Max_with_arg(true);
    [ConditionalFact] public Task Max_no_data() => _cases.Max_no_data(true);
    [ConditionalFact] public Task Max_no_data_cast_to_nullable() => _cases.Max_no_data_cast_to_nullable(true);
    [ConditionalFact] public Task Count_with_no_predicate() => _cases.Count_with_no_predicate(true);
    [ConditionalFact] public Task Count_with_predicate() => _cases.Count_with_predicate(true);
    [ConditionalFact] public Task Count_with_order_by() => _cases.Count_with_order_by(true);
    private sealed class Cases(NorthwindAggregatesNeo4jFixture fixture) : NorthwindAggregateOperatorsQueryTestBase<NorthwindAggregatesNeo4jFixture>(fixture);
}
