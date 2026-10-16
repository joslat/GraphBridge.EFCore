using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Benchmarks;

public sealed class EdgeBenchmarkSource
{
    public Guid Id { get; set; }
    public string Run { get; set; } = "";
    public List<EdgeBenchmarkEdge> Edges { get; } = [];
}

public sealed class EdgeBenchmarkTarget
{
    public Guid Id { get; set; }
    public string Run { get; set; } = "";
    public List<EdgeBenchmarkEdge> Edges { get; } = [];
}

public sealed class EdgeBenchmarkEdge
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public string Run { get; set; } = "";
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public long Version { get; set; }
    public EdgeBenchmarkSource Source { get; set; } = null!;
    public EdgeBenchmarkTarget Target { get; set; } = null!;
}

public sealed class EdgeBenchmarkContext(DbContextOptions<EdgeBenchmarkContext> options) : DbContext(options)
{
    public DbSet<EdgeBenchmarkEdge> Edges => Set<EdgeBenchmarkEdge>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var source = model.Entity<EdgeBenchmarkSource>().ToGraphNode(EdgeBenchmarkData.SourceLabel);
        source.HasKey(p => p.Id);
        source.Property(p => p.Id).ValueGeneratedNever();
        var target = model.Entity<EdgeBenchmarkTarget>().ToGraphNode(EdgeBenchmarkData.TargetLabel);
        target.HasKey(p => p.Id);
        target.Property(p => p.Id).ValueGeneratedNever();
        var edge = model.Entity<EdgeBenchmarkEdge>();
        edge.HasKey(p => p.Id);
        edge.Property(p => p.Id).ValueGeneratedNever();
        edge.Property(p => p.Version).IsRowVersion();
        edge.HasOne(p => p.Source).WithMany(p => p.Edges).HasForeignKey(p => p.SourceId);
        edge.HasOne(p => p.Target).WithMany(p => p.Edges).HasForeignKey(p => p.TargetId);
        edge.ToGraphIdentifiedRelationship(EdgeBenchmarkData.RelationshipType, nameof(EdgeBenchmarkEdge.Id),
            [nameof(EdgeBenchmarkEdge.SourceId)], [nameof(EdgeBenchmarkEdge.TargetId)]);
    }
}

/// <summary>The independent direct control mirrors the provider's returned-identity checks, not global graph validation.</summary>
public static class EdgeBenchmarkData
{
    public const string SourceLabel = "GraphBridgeBenchmarkEdgeSource";
    public const string TargetLabel = "GraphBridgeBenchmarkEdgeTarget";
    public const string RelationshipType = "GRAPHBRIDGE_BENCHMARK_IDENTIFIED_EDGE";
    public const string ConstraintsQuery = "SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties";

    // Same scope/order as CypherQueryRenderer.AppendIdentifiedEdgeCount: all physical relationships
    // with this type/application ID, independent of endpoints, payload filters and row ordering.
    private const string ReadRoot = """
        MATCH (s:GraphBridgeBenchmarkEdgeSource)-[r:GRAPHBRIDGE_BENCHMARK_IDENTIFIED_EDGE]->(t:GraphBridgeBenchmarkEdgeTarget)
        CALL (r) {
          MATCH ()-[candidate:GRAPHBRIDGE_BENCHMARK_IDENTIFIED_EDGE]->()
          WHERE candidate.Id = r.Id
          WITH candidate LIMIT 2
          RETURN count(*) AS IdentityCount
        }
        WITH s, r, t, IdentityCount
        WHERE coalesce(r.Run = $run, (r.Run IS NULL AND $run IS NULL))
        RETURN r.Id AS Id, s.Id AS SourceId, t.Id AS TargetId, r.Ordinal AS Ordinal, IdentityCount AS IdentityCount
        """;

    public static string ReadQuery(bool scalar) => ReadRoot
        + (scalar ? "" : ", r.Run AS Run, r.Name AS Name, r.Version AS Version")
        + "\nORDER BY r.Ordinal IS NULL DESC, r.Ordinal ASC";

    public static void ValidateCounts(IReadOnlyList<int> counts)
    {
        if (counts.Count == 0 || counts.Distinct().Count() != counts.Count || counts.Any(x => x is not (1 or 100 or 10000)))
            throw new ArgumentException("Edge counts must be a unique nonempty subset of 1, 100 and 10000.", nameof(counts));
    }

    public static int SourceCount(int edges) => Math.Min(10, edges);
    public static int TargetCount(int edges) => Math.Min(100, edges);
    public static Guid SourceKey(string run, int ordinal) => Key(run, ordinal);
    public static Guid TargetKey(string run, int ordinal) => Key(run, 0x100 + ordinal);
    public static Guid EdgeKey(string run, int ordinal) => Key(run, 0x10000 + ordinal);
    private static Guid Key(string run, int suffix)
    {
        if (!Guid.TryParseExact(run, "N", out _) || run != run.ToLowerInvariant() || suffix < 0)
            throw new ArgumentException("A generated lowercase run ID and nonnegative suffix are required.");
        return Guid.ParseExact(run[..24] + suffix.ToString("x8", CultureInfo.InvariantCulture), "N");
    }

    public static EdgeBenchmarkEdge Expected(string run, int ordinal, int count) => new()
    {
        Id = EdgeKey(run, ordinal), SourceId = SourceKey(run, ordinal % SourceCount(count)),
        TargetId = TargetKey(run, ordinal % TargetCount(count)), Run = run, Ordinal = ordinal,
        Name = "edge-" + ordinal.ToString(CultureInfo.InvariantCulture), Version = 1
    };

    public static EdgeBenchmarkRow Decode(IReadOnlyDictionary<string, object?> row, bool scalar)
    {
        static T Required<T>(IReadOnlyDictionary<string, object?> values, string name)
            => values.TryGetValue(name, out var value) && value is T typed ? typed
                : throw new InvalidOperationException("A required edge benchmark wire value is missing or has the wrong type.");
        static Guid Canonical(IReadOnlyDictionary<string, object?> values, string name)
        {
            var wire = Required<string>(values, name);
            var value = Guid.Parse(wire);
            if (wire != value.ToString("D")) throw new InvalidOperationException("Noncanonical edge benchmark key.");
            return value;
        }
        if (Required<long>(row, "IdentityCount") != 1)
            throw new InvalidOperationException("Identified edge must have exactly one physical relationship for its type/application ID.");
        var id = Canonical(row, "Id");
        var source = Canonical(row, "SourceId");
        var target = Canonical(row, "TargetId");
        var ordinal = checked((int)Required<long>(row, "Ordinal"));
        return new(id, source, target, ordinal, scalar ? null : new EdgeBenchmarkEdge
        {
            Id = id, SourceId = source, TargetId = target, Ordinal = ordinal,
            Run = Required<string>(row, "Run"), Name = Required<string>(row, "Name"),
            Version = Required<long>(row, "Version")
        });
    }
}

public readonly record struct EdgeBenchmarkRow(Guid Id, Guid SourceId, Guid TargetId, int Ordinal, EdgeBenchmarkEdge? Entity);
