using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using static GraphBridge.UnitTests.IdentifiedEdgeBindingTests;

namespace GraphBridge.UnitTests;

public sealed class IdentifiedEdgeMutationTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public async Task New_endpoints_and_parallel_edges_plan_CREATE_by_distinct_ID_and_count_each_EF_entry()
    {
        using var context = Context();
        var source = new Source();
        var target = new Target();
        context.AddRange(source, target);
        var first = new Event { SourceId = source.Id, TargetId = target.Id, Role = "O'Reilly `$id` MATCH (n) DELETE n" };
        var second = new Event { SourceId = source.Id, TargetId = target.Id };
        context.AddRange(first, second);
        var plan = await Plan(context);
        Assert.Equal(4, plan.ExpectedStateEntries);
        Assert.Equal(new[] { GraphMutationPhase.NodeCreate, GraphMutationPhase.NodeCreate,
            GraphMutationPhase.AssociationAdd, GraphMutationPhase.AssociationAdd }, plan.Operations.Select(o => o.Phase));
        Assert.Equal(2, plan.RequiredNodeBindings.Count);
        Assert.Single(plan.RequiredIdentifiedEdges);
        var edges = plan.Operations.Skip(2).ToArray();
        Assert.NotEqual(first.Id, second.Id);
        Assert.All(edges, edge =>
        {
            Assert.Contains("CREATE (s)-[r:`EVENT`]->(t)", edge.Command.Text);
            Assert.DoesNotContain("MERGE", edge.Command.Text);
            Assert.Equal(2, edge.LockCommands.Count);
            var properties = Map(edge.Command, "properties");
            Assert.False(properties.ContainsKey("SourceId"));
            Assert.False(properties.ContainsKey("TargetId"));
            Assert.Equal(1L, properties["Version"]);
        });
        Assert.Equal(first.Id.ToString("D"), Map(edges[0].Command, "properties")["event_key"]);
        Assert.DoesNotContain(first.Role!, edges[0].Command.Text);
        Assert.Equal(0L, first.Version);
        Assert.Equal(2, plan.GeneratedValues.Count);
    }

    [Fact]
    public async Task Modified_payload_uses_original_ID_endpoints_tokens_and_selective_changes()
    {
        using var context = Context();
        var edge = Existing();
        context.Attach(edge);
        edge.Role = "new";
        edge.Note = null;
        edge.Token = "new token";
        var operation = Assert.Single((await Plan(context)).Operations);
        Assert.Equal(edge.Id.ToString("D"), operation.Command.Parameters["id"]);
        Assert.Equal(edge.SourceId.ToString("D"), operation.Command.Parameters["s0"]);
        Assert.Equal(edge.TargetId.ToString("D"), operation.Command.Parameters["t0"]);
        Assert.Contains("MATCH (a)-[r:`EVENT` {`event_key`: $id}]->(b)", operation.Command.Text);
        Assert.Contains("candidates[0].source = s AND candidates[0].target = t", operation.Command.Text);
        Assert.Contains("SET r.`event_key` = r.`event_key`", operation.Command.Text);
        Assert.Equal("old token", operation.Command.Parameters["o0"]);
        Assert.Equal(7L, operation.Command.Parameters["o1"]);
        var changes = Map(operation.Command, "changes");
        Assert.Equal(4, changes.Count);
        Assert.Equal("new", changes["Role"]);
        Assert.Null(changes["Note"]);
        Assert.Equal(8L, changes["Version"]);
        Assert.Equal(7L, edge.Version);
    }

    [Fact]
    public async Task Deleted_edge_is_ID_bound_and_never_deletes_an_endpoint_or_publishes_version()
    {
        using var context = Context();
        var edge = Existing();
        context.Attach(edge);
        context.Remove(edge);
        var operation = Assert.Single((await Plan(context)).Operations);
        Assert.Equal(GraphMutationPhase.AssociationDelete, operation.Phase);
        Assert.Contains("FOREACH (r IN eligible | DELETE r)", operation.Command.Text);
        Assert.DoesNotContain("DELETE s", operation.Command.Text);
        Assert.Empty(operation.GeneratedValues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_rebinding_rejects_modified_or_deleted_edge_before_any_plan(bool delete)
    {
        using var context = Context();
        var edge = Existing();
        context.Attach(edge);
        edge.TargetId = Guid.NewGuid();
        context.ChangeTracker.DetectChanges();
        if (delete) context.Remove(edge);
        Assert.Contains("rebinding", (await Assert.ThrowsAsync<NotSupportedException>(() => Plan(context))).Message);
        Assert.Null(context.Capture.Plan);
    }

    [Fact]
    public async Task Temporary_ID_and_version_overflow_fail_before_execution()
    {
        using var temporary = Context();
        var added = Existing();
        temporary.Add(added);
        temporary.Capture.BeforePlan = entries =>
        {
            var entry = Assert.Single(entries);
            entry.ToEntityEntry().Property(nameof(Event.Id)).IsTemporary = true;
        };
        Assert.Contains("stable client value", (await Assert.ThrowsAsync<InvalidOperationException>(() => Plan(temporary))).Message);
        using var overflow = Context();
        var edge = Existing();
        edge.Version = long.MaxValue;
        overflow.Attach(edge);
        edge.Role = "overflow";
        await Assert.ThrowsAsync<OverflowException>(() => Plan(overflow));
    }

    [Fact]
    public async Task Lossy_application_ID_converter_rejects_before_planning()
    {
        using var context = new AssociationMutationTests.PlanContext(_driver, model =>
        {
            Configure(model);
            model.Entity<Event>().Property(e => e.Id).HasConversion(
                value => "00000000-0000-0000-0000-000000000001", value => Guid.Parse(value));
        });
        context.Add(Existing());
        Assert.Contains("round-trip", (await Assert.ThrowsAsync<InvalidOperationException>(() => Plan(context))).Message);
    }

    [Fact]
    public void Renderer_guards_invalid_ID_names_and_mutable_ID_payload()
    {
        var keys = new Dictionary<string, object?> { ["id"] = "endpoint" };
        foreach (var id in new object?[] { null, "", " " })
            Assert.Throws<InvalidOperationException>(() => IdentifiedEdgeMutationRenderer.Create("EVENT", "edgeId", id!, "Source", keys, "Target", keys, new Dictionary<string, object?>()));
        Assert.Throws<ArgumentException>(() => IdentifiedEdgeMutationRenderer.Create("bad`type", "edgeId", "id", "Source", keys, "Target", keys, new Dictionary<string, object?>()));
        Assert.Throws<InvalidOperationException>(() => IdentifiedEdgeMutationRenderer.Update("EVENT", "edgeId", "id", "Source", keys, "Target", keys,
            new Dictionary<string, object?>(), new Dictionary<string, object?> { ["edgeId"] = "replacement" }));
    }

    private AssociationMutationTests.PlanContext Context() => new(_driver, Configure);
    private static Event Existing() => new()
    {
        Id = Guid.NewGuid(), SourceId = Guid.NewGuid(), TargetId = Guid.NewGuid(),
        Role = "old", Note = "remove", Token = "old token", Version = 7
    };
    private static async Task<GraphMutationPlan> Plan(AssociationMutationTests.PlanContext context)
    {
        var count = await context.SaveChangesAsync(false);
        var plan = Assert.IsType<GraphMutationPlan>(context.Capture.Plan);
        Assert.Equal(count, plan.ExpectedStateEntries);
        return plan;
    }
    private static IReadOnlyDictionary<string, object?> Map(CypherMutationCommand command, string name)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(command.Parameters[name]);
}
