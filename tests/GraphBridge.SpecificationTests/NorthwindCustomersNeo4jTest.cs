using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class NorthwindCustomersNeo4jTest : IClassFixture<NorthwindCustomersNeo4jFixture>
{
    private readonly NorthwindCustomersNeo4jFixture _fixture;
    private readonly WhereCases _where;
    private readonly SelectCases _select;
    private readonly NoTrackingCases _noTracking;

    public NorthwindCustomersNeo4jTest(NorthwindCustomersNeo4jFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _where = new WhereCases(fixture);
        _select = new SelectCases(fixture);
        _noTracking = new NoTrackingCases(fixture);
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    [ConditionalFact] public Task Where_simple() => _where.Where_simple(true);
    [ConditionalFact] public async Task Where_simple_closure() => _ = await _where.Where_simple_closure(true);
    [ConditionalFact] public Task Where_indexer_closure() => _where.Where_indexer_closure(true);
    [ConditionalFact] public Task Where_dictionary_key_access_closure() => _where.Where_dictionary_key_access_closure(true);
    [ConditionalFact] public Task Where_tuple_item_closure() => _where.Where_tuple_item_closure(true);
    [ConditionalFact] public Task Where_named_tuple_item_closure() => _where.Where_named_tuple_item_closure(true);
    [ConditionalFact] public Task Where_simple_closure_constant() => _where.Where_simple_closure_constant(true);
    [ConditionalFact] public Task Where_simple_closure_via_query_cache() => _where.Where_simple_closure_via_query_cache(true);
    [ConditionalFact] public Task Where_method_call_closure_via_query_cache() => _where.Where_method_call_closure_via_query_cache(true);
    [ConditionalFact] public Task Where_field_access_closure_via_query_cache() => _where.Where_field_access_closure_via_query_cache(true);
    [ConditionalFact] public Task Where_property_access_closure_via_query_cache() => _where.Where_property_access_closure_via_query_cache(true);

    [ConditionalFact]
    public async Task Select_scalar()
    {
        await _select.Select_scalar(true);
        Assert.Equal("MATCH (n:`GraphBridgeSpecCustomer`)\nRETURN n.`City` AS `c2`", Assert.Single(Commands()));
    }
    [ConditionalFact] public Task Select_customer_table() => _select.Select_customer_table(true);
    [ConditionalFact] public Task Select_customer_identity() => _select.Select_customer_identity(true);
    [ConditionalFact] public Task Select_into() => _select.Select_into(true);
    [ConditionalFact] public Task Select_anonymous_one() => _select.Select_anonymous_one(true);
    [ConditionalFact] public Task Select_anonymous_two() => _select.Select_anonymous_two(true);
    [ConditionalFact] public Task Select_anonymous_three() => _select.Select_anonymous_three(true);
    [ConditionalFact] public Task Select_anonymous_bool_constant_true() => _select.Select_anonymous_bool_constant_true(true);
    [ConditionalFact] public Task Select_anonymous_with_object() => _select.Select_anonymous_with_object(true);
    [ConditionalFact] public Task Select_anonymous_nested() => _select.Select_anonymous_nested(true);
    [ConditionalFact] public Task Select_anonymous_empty() => _select.Select_anonymous_empty(true);
    [ConditionalFact] public Task Select_anonymous_literal() => _select.Select_anonymous_literal(true);
    [ConditionalFact] public Task No_tracking_entity() => _noTracking.Entity_not_added_to_state_manager(false, true);
    [ConditionalFact] public Task No_tracking_explicit_parameter() => _noTracking.Entity_not_added_to_state_manager(true, true);
    [ConditionalFact] public Task No_tracking_reads_current_database_values() => _noTracking.Can_get_current_values(true);

    private string[] Commands() => _fixture.ListLoggerFactory.Log.SelectMany(e =>
        e.State as IEnumerable<KeyValuePair<string, object?>> ?? []).Where(p => p.Key == "Cypher")
        .Select(p => Assert.IsType<string>(p.Value)).ToArray();

    private sealed class WhereCases(NorthwindCustomersNeo4jFixture fixture) : NorthwindWhereQueryTestBase<NorthwindCustomersNeo4jFixture>(fixture);
    private sealed class SelectCases(NorthwindCustomersNeo4jFixture fixture) : NorthwindSelectQueryTestBase<NorthwindCustomersNeo4jFixture>(fixture);
    private sealed class NoTrackingCases(NorthwindCustomersNeo4jFixture fixture) : NorthwindAsNoTrackingQueryTestBase<NorthwindCustomersNeo4jFixture>(fixture);
}
