using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class NorthwindRelationshipsNeo4jTest : IClassFixture<NorthwindRelationshipsNeo4jFixture>
{
    private readonly IncludeCases _cases;
    public NorthwindRelationshipsNeo4jTest(NorthwindRelationshipsNeo4jFixture fixture, ITestOutputHelper output)
    {
        _cases = new IncludeCases(fixture);
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    [ConditionalFact] public Task Include_collection_orderby_take() => _cases.Include_collection_orderby_take(true);
    [ConditionalFact] public Task Include_collection_single_or_default_no_result() => _cases.Include_collection_single_or_default_no_result(true);
    [ConditionalFact] public Task Include_collection_with_filter() => _cases.Include_collection_with_filter(true);
    [ConditionalFact] public Task Include_collection_with_filter_reordered() => _cases.Include_collection_with_filter_reordered(true);
    [ConditionalFact] public Task Include_reference_single_or_default_when_no_result() => _cases.Include_reference_single_or_default_when_no_result(true);
    [ConditionalFact] public Task Include_reference_with_filter() => _cases.Include_reference_with_filter(true);
    [ConditionalFact] public Task Include_reference_with_filter_reordered() => _cases.Include_reference_with_filter_reordered(true);

    private sealed class IncludeCases(NorthwindRelationshipsNeo4jFixture fixture)
        : NorthwindIncludeQueryTestBase<NorthwindRelationshipsNeo4jFixture>(fixture);
}
