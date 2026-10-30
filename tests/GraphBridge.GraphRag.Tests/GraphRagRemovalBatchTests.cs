using GraphBridge.GraphRag;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-66 (ADR 0052, section 19) offline: how documents are cut into the batches one transaction removes.</summary>
public sealed class GraphRagRemovalBatchTests
{
    [Fact]
    public void Batches_hold_at_most_a_hundred_documents_in_order()
    {
        var batches = GraphRagRemoval.Batches(Enumerable.Range(0, 250).Select(i => ($"d{i:000}", 1_000L))).ToArray();
        Assert.Equal([100, 100, 50], batches.Select(b => b.Length));
        Assert.Equal(Enumerable.Range(0, 250).Select(i => $"d{i:000}"), batches.SelectMany(b => b));
    }

    [Fact]
    public void Batches_hold_at_most_eight_megabytes_of_text_but_a_larger_document_goes_alone()
    {
        const long MB = 1 << 20;
        var batches = GraphRagRemoval.Batches([("a", 3 * MB), ("b", 3 * MB), ("c", 3 * MB), ("big", 20 * MB), ("d", 1 * MB)]).ToArray();
        Assert.Equal([["a", "b"], ["c"], ["big"], ["d"]], batches);
        Assert.Empty(GraphRagRemoval.Batches([]));
    }
}
