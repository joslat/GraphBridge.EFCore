using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.UnitTests;

public sealed class AssociationRowGuardTests(ITestOutputHelper output) : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("zero")]
    [InlineData("two")]
    [InlineData("negative")]
    [InlineData("integer")]
    [InlineData("text")]
    public void Invalid_cardinality_rejects_before_even_endpoint_conversion(string fault)
    {
        using var context = Context();
        var read = Binding(context);
        var row = Row(read);
        row[Slot(read, nameof(Edge.SourceId)).Column.Alias] = "malformed guid";
        row[CypherQueryPlan.AssociationCountAlias] = fault switch
        {
            "null" => null, "zero" => 0L, "two" => 2L, "negative" => -1L,
            "integer" => (object)1, "text" => "1", _ => 1L
        };
        if (fault == "missing") row.Remove(CypherQueryPlan.AssociationCountAlias);
        Assert.Contains("cardinality diagnostic", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void One_relationship_decodes_all_EF_slots_with_required_values_and_nullable_payload()
    {
        using var context = Context();
        var read = Binding(context);
        var values = read.ReadRow(Row(read));
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), values[Slot(read, nameof(Edge.SourceId)).Index]);
        Assert.Equal("lead", values[Slot(read, nameof(Edge.Role)).Index]);
        Assert.Equal(7, values[Slot(read, nameof(Edge.Score)).Index]);
        Assert.Null(values[Slot(read, nameof(Edge.Note)).Index]);
    }

    [Fact]
    public void Scalar_plan_carries_and_decodes_hidden_endpoint_keys_without_requiring_other_payloads()
    {
        using var context = Context();
        var read = Binding(context);
        var role = Slot(read, nameof(Edge.Role));
        var plan = read.CreatePlan([role]);
        Assert.Equal(3, plan.Columns.Count);
        Assert.Contains(plan.Columns, c => c.Owner == CypherPropertyOwner.Source);
        Assert.Contains(plan.Columns, c => c.Owner == CypherPropertyOwner.Target);
        var row = Row(read);
        row.Remove(Slot(read, nameof(Edge.Score)).Column.Alias);
        row.Remove(Slot(read, nameof(Edge.Note)).Column.Alias);
        Assert.Equal("lead", read.ReadRow(row, [role])[role.Index]);
        Assert.Contains(CypherQueryPlan.AssociationCountAlias, CypherQueryRenderer.Render(plan));
        row[Slot(read, nameof(Edge.SourceId)).Column.Alias] = null;
        Assert.Contains("required property", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, [role])).Message);
    }

    [Fact]
    public void Scalar_decode_rejects_missing_hidden_key_even_with_valid_count()
    {
        using var context = Context();
        var read = Binding(context);
        var row = Row(read);
        row.Remove(Slot(read, nameof(Edge.TargetId)).Column.Alias);
        Assert.Throws<KeyNotFoundException>(() => read.ReadRow(row, [Slot(read, nameof(Edge.Role))]));
    }

    [Fact]
    public void Projection_rejects_duplicate_or_foreign_slots()
    {
        using var context = Context();
        var read = Binding(context);
        var role = Slot(read, nameof(Edge.Role));
        Assert.Throws<InvalidOperationException>(() => read.CreatePlan([role, role]));
        var foreign = role with { StoreProperty = new CypherProperty("different", CypherPropertyOwner.Source) };
        Assert.Throws<InvalidOperationException>(() => read.ReadRow(Row(read), [foreign]));
    }

    [Fact]
    public void Prepared_projection_snapshots_slots_reads_without_enumerating_and_does_not_reuse_buffers()
    {
        using var context = Context();
        var read = Binding(context);
        var role = Slot(read, nameof(Edge.Role));
        var projection = new List<GraphReadProperty> { role };
        var reader = read.PrepareRead(projection);
        projection.Clear();
        projection.Add(role with { StoreProperty = new CypherProperty("foreign") });
        var row = Row(read);
        row.Remove(Slot(read, nameof(Edge.Score)).Column.Alias);
        row.Remove(Slot(read, nameof(Edge.Note)).Column.Alias);
        var lookup = new LookupOnlyRow(row);
        var first = reader.ReadRow(lookup);
        row[role.Column.Alias] = "support";
        var second = reader.ReadRow(lookup);
        Assert.Equal("lead", first[role.Index]);
        Assert.Equal("support", second[role.Index]);
        row[Slot(read, nameof(Edge.SourceId)).Column.Alias] = null;
        Assert.Contains("required property", Assert.Throws<InvalidOperationException>(() => reader.ReadRow(lookup)).Message);
        row[CypherQueryPlan.AssociationCountAlias] = 2L;
        Assert.Contains("cardinality diagnostic", Assert.Throws<InvalidOperationException>(() => reader.ReadRow(lookup)).Message);
    }

    [Fact]
    public void Prepared_reader_is_reentrant_across_independent_rows()
    {
        using var context = Context();
        var read = Binding(context);
        var role = Slot(read, nameof(Edge.Role));
        var reader = read.PrepareRead([role]);
        var rows = Enumerable.Range(0, 32).Select(i =>
        {
            var row = Row(read);
            row[role.Column.Alias] = "role-" + i;
            return row;
        }).ToArray();
        var values = new Microsoft.EntityFrameworkCore.Storage.ValueBuffer[rows.Length];
        Parallel.For(0, rows.Length, i => values[i] = reader.ReadRow(new LookupOnlyRow(rows[i])));
        for (var i = 0; i < values.Length; i++) Assert.Equal("role-" + i, values[i][role.Index]);
    }

    [Fact]
    public void Prepared_relationship_decoder_stays_within_per_record_allocation_budget()
    {
        using var context = Context();
        var read = Binding(context);
        var reader = read.PrepareRead();
        var row = Row(read);
        var score = Slot(read, nameof(Edge.Score)).Index;
        for (var i = 0; i < 32; i++) _ = reader.ReadRow(row);
        const int count = 1000;
        var checksum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) checksum += (int)reader.ReadRow(row)[score]!;
        var perRecord = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)count;
        output.WriteLine("Prepared relationship decoder: {0:F2} allocated bytes/record over {1} synchronous rows.", perRecord, count);
        Assert.Equal(7 * count, checksum);
        // Includes a fresh buffer, two Guid decodes/canonical re-encodes and payload
        // conversion. Room for runtime variation; repeated projection/map preparation
        // must not consume kilobytes per row. This excludes EF shaping/driver work.
        Assert.InRange(perRecord, 1, 1024);
    }

    private sealed class LookupOnlyRow(IReadOnlyDictionary<string, object?> values) : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => values[key];
        public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public int Count => throw new InvalidOperationException("Do not inspect or copy the entire driver map.");
        public IEnumerable<string> Keys => throw new InvalidOperationException("Do not enumerate driver keys.");
        public IEnumerable<object?> Values => throw new InvalidOperationException("Do not enumerate driver values.");
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("Do not copy the driver map.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Endpoint_schema_requirements_are_immutable_and_self_association_deduplicates_the_node_binding()
    {
        using var context = Context();
        var read = Binding(context);
        Assert.Equal(2, read.RequiredUniqueEndpoints.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<NodeBinding>)read.RequiredUniqueEndpoints).Clear());
        var compiler = context.GetService<GraphBindingCompiler>();
        var self = GraphReadBinding.FromAssociation(compiler.GetAssociation(context.Model.FindEntityType(typeof(SelfEdge))!));
        Assert.Single(self.RequiredUniqueEndpoints);
        var node = GraphReadBinding.FromNode(compiler.GetNode(context.Model.FindEntityType(typeof(Source))!));
        Assert.Empty(node.RequiredUniqueEndpoints);
        var id = Guid.NewGuid();
        var row = new Dictionary<string, object?> { [node.Properties[0].Column.Alias] = id.ToString("D") };
        Assert.Equal(id, node.ReadRow(row)[node.Properties[0].Index]);
    }

    private ProviderModelContext Context() => new(_driver, model =>
    {
        model.Entity<Source>().ToGraphNode("GuardSource").Property(e => e.Id).HasGraphPropertyName("sourceKey");
        model.Entity<Target>().ToGraphNode("GuardTarget").Property(e => e.Id).HasGraphPropertyName("targetKey");
        var edge = model.Entity<Edge>();
        edge.HasKey(e => new { e.SourceId, e.TargetId });
        edge.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
        edge.HasOne<Target>().WithMany().HasForeignKey(e => e.TargetId);
        edge.ToGraphRelationship("GUARD_LINK", [nameof(Edge.SourceId)], [nameof(Edge.TargetId)]);
        var self = model.Entity<SelfEdge>();
        self.HasKey(e => new { e.SourceId, e.TargetId });
        self.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
        self.HasOne<Source>().WithMany().HasForeignKey(e => e.TargetId);
        self.ToGraphRelationship("GUARD_SELF", [nameof(SelfEdge.SourceId)], [nameof(SelfEdge.TargetId)]);
    });
    private static GraphReadBinding Binding(ProviderModelContext context)
        => GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>().GetAssociation(context.Model.FindEntityType(typeof(Edge))!));
    private static GraphReadProperty Slot(GraphReadBinding read, string name)
        => read.Properties.Single(p => p.ModelProperty.Property.Name == name);
    private static Dictionary<string, object?> Row(GraphReadBinding read)
    {
        var row = read.Properties.ToDictionary(p => p.Column.Alias, p => p.ModelProperty.Property.Name switch
        {
            nameof(Edge.SourceId) => (object?)"00000000-0000-0000-0000-000000000001",
            nameof(Edge.TargetId) => "00000000-0000-0000-0000-000000000002",
            nameof(Edge.Role) => "lead", nameof(Edge.Score) => 7L, _ => null
        });
        row[CypherQueryPlan.AssociationCountAlias] = 1L;
        return row;
    }
    public sealed class Source { public Guid Id { get; set; } }
    public sealed class Target { public Guid Id { get; set; } }
    public sealed class Edge
    {
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string Role { get; set; } = "";
        public int Score { get; set; }
        public string? Note { get; set; }
    }
    public sealed class SelfEdge { public Guid SourceId { get; set; } public Guid TargetId { get; set; } }
}
