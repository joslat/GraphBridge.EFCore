using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestModels.UpdatesModel;
using Microsoft.EntityFrameworkCore.Update;
using Xunit.Abstractions;

namespace GraphBridge.SpecificationTests;

[Collection(SpecificationCollection.Name), RequiresDisposableNeo4j]
public sealed class UpdatesNeo4jTest : IClassFixture<UpdatesNeo4jFixture>
{
    private readonly UpdateCases _cases;
    public UpdatesNeo4jTest(UpdatesNeo4jFixture fixture, ITestOutputHelper output)
    {
        _cases = new UpdateCases(fixture);
        if (RequiresDisposableNeo4jAttribute.Enabled)
        {
            fixture.ListLoggerFactory.Clear();
            fixture.ListLoggerFactory.SetTestOutputHelper(output);
        }
    }

    [ConditionalFact] public Task Save_partial_update() => _cases.Save_partial_update();
    [ConditionalFact] public Task Save_partial_update_on_missing_record_throws() => _cases.Save_partial_update_on_missing_record_throws();
    [ConditionalFact] public Task Save_partial_update_on_concurrency_token_original_value_mismatch_throws() => _cases.Save_partial_update_on_concurrency_token_original_value_mismatch_throws();
    [ConditionalFact] public Task Can_remove_partial() => _cases.Can_remove_partial();
    [ConditionalFact] public Task Remove_partial_on_missing_record_throws() => _cases.Remove_partial_on_missing_record_throws();
    [ConditionalFact] public Task Remove_partial_on_concurrency_token_original_value_mismatch_throws() => _cases.Remove_partial_on_concurrency_token_original_value_mismatch_throws();
    [ConditionalFact] public Task Mutation_of_tracked_values_does_not_mutate_values_in_store() => _cases.Mutation_of_tracked_values_does_not_mutate_values_in_store();
    [ConditionalFact] public Task Update_on_bytes_concurrency_token_original_value_mismatch_throws() => _cases.Update_on_bytes_concurrency_token_original_value_mismatch_throws();
    [ConditionalFact] public Task Update_on_bytes_concurrency_token_original_value_matches_does_not_throw() => _cases.Update_on_bytes_concurrency_token_original_value_matches_does_not_throw();
    [ConditionalFact] public Task Remove_on_bytes_concurrency_token_original_value_mismatch_throws() => _cases.Remove_on_bytes_concurrency_token_original_value_mismatch_throws();
    [ConditionalFact] public Task Remove_on_bytes_concurrency_token_original_value_matches_does_not_throw() => _cases.Remove_on_bytes_concurrency_token_original_value_matches_does_not_throw();

    private sealed class UpdateCases(UpdatesNeo4jFixture fixture) : UpdatesTestBase<UpdatesNeo4jFixture>(fixture)
    {
        protected override string UpdateConcurrencyMessage => "The graph mutation did not affect exactly one graph element; an endpoint, application key, original concurrency token, or association cardinality is missing, changed, or ambiguous.";
        protected override string UpdateConcurrencyTokenMessage => UpdateConcurrencyMessage;

        protected override async Task ExecuteWithStrategyInTransactionAsync(
            Func<UpdatesContext, Task> testOperation,
            Func<UpdatesContext, Task>? nestedTestOperation1 = null,
            Func<UpdatesContext, Task>? nestedTestOperation2 = null)
        {
            // The upstream helper shares one transaction between contexts and disposes it synchronously.
            // This named adapter instead commits each real SaveChangesAsync, reads in fresh contexts,
            // and reseeds between serialized tests. No cross-context transaction/retry claim is made.
            try
            {
                foreach (var operation in new[] { testOperation, nestedTestOperation1, nestedTestOperation2 })
                {
                    if (operation is null) continue;
                    await using var context = Fixture.CreateContext();
                    await operation(context);
                }
            }
            finally { await Fixture.ReseedAsync(); }
        }
    }
}
