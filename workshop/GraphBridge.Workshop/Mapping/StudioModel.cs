using GraphBridge.Generated;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Mapping;

// workshop:start Model
// The attributes are the whole mapping. The source generator turns them into StudioMapping.Apply and
// StudioMapping.Verify at build time; mistakes become compiler errors (GBSG001–GBSG008), not runtime surprises.
[GraphModel(typeof(Studio), typeof(Release), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
public static partial class StudioMapping { }

[GraphNode("Studio", nameof(Id))]
public sealed class Studio
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("name", true)] public string Name { get; set; } = "";
    [GraphProperty("founded", false)] public int? Founded { get; set; }
    [GraphIgnore] public string Display => $"{Name} ({Founded})";
}

[GraphNode("Release", nameof(Id))]
public sealed class Release
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("title", true)] public string Title { get; set; } = "";
    [GraphProperty("version", true, Concurrency = GraphConcurrency.IncrementingInt64)] public long Version { get; set; }
}
// workshop:end Model

/// <summary>A context whose whole model comes from the generated mapping.</summary>
public sealed class StudioContext(IDriver driver) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNeo4j(driver);
    protected override void OnModelCreating(ModelBuilder modelBuilder) => StudioMapping.Apply(modelBuilder);
}
