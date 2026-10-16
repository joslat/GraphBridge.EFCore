using GraphBridge.Generated;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Generators.Consumer;

[GraphModel(typeof(Person), typeof(Company), typeof(Employment), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
public static partial class OrganizationMapping { }

[GraphNode("GeneratorPerson", nameof(Tenant), nameof(Id), Iri = "urn:graphbridge:Person")]
public sealed class Person
{
    [GraphProperty("tenant", true)] public string Tenant { get; set; } = "";
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("name", false, Iri = "urn:graphbridge:name")] public string? Name { get; set; }
    [GraphProperty("version", true, Concurrency = GraphConcurrency.IncrementingInt64)] public long Version { get; set; }
    [GraphIgnore] public string Display => Name ?? Tenant;
}

[GraphNode("GeneratorCompany", nameof(Id))]
public sealed class Company
{
    [GraphProperty("id", true)] public long Id { get; set; }
    [GraphProperty("enabled", true)] public bool Enabled { get; set; }
}

[GraphIdentifiedEdge("GENERATOR_WORKS_FOR", nameof(Id))]
public sealed class Employment
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("personTenant", true)] public string PersonTenant { get; set; } = "";
    [GraphProperty("personId", true, Conversion = GraphConversion.GuidString)] public Guid PersonId { get; set; }
    [GraphProperty("companyId", true)] public long CompanyId { get; set; }
    [GraphProperty("role", false, Concurrency = GraphConcurrency.OriginalValue)] public string? Role { get; set; }
    [GraphProperty("since", true, Conversion = GraphConversion.DateOnlyIsoString)] public DateOnly Since { get; set; }
    [GraphProperty("until", false, Conversion = GraphConversion.DateOnlyIsoString)] public DateOnly? Until { get; set; }
    [GraphProperty("rank", false)] public int? Rank { get; set; }
    [GraphProperty("weight", true)] public double Weight { get; set; }
    [GraphProperty("version", true, Concurrency = GraphConcurrency.IncrementingInt64)] public long Version { get; set; }
    [GraphSource(nameof(PersonTenant), nameof(PersonId))] public Person Person { get; set; } = null!;
    [GraphTarget(nameof(CompanyId))] public Company Company { get; set; } = null!;
}

public sealed class ConsumerContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => OrganizationMapping.Apply(modelBuilder);
}
