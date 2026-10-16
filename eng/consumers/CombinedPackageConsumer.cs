using System.Reflection;
using System.Text.Json;
using GraphBridge.Generated;
using GraphBridge.Tooling.Semantics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

var driver = DispatchProxy.Create<IDriver, RejectTransport>();
var probe = (RejectTransport)(object)driver;
using (var db = new CombinedContext(driver))
{
    var manifest = db.Database.GetGraphSchemaManifest();
    Require(manifest.Nodes.Single().Label == "CombinedPackageItem", "Packaged generator mapping did not reach the provider model.");
    var privateValue = "consumer-sensitive-value";
    var text = db.Set<Item>().Where(x => x.Name == privateValue).Take(2).ToQueryString();
    Require(text.Contains("MATCH (n:`CombinedPackageItem`)") && !text.Contains(privateValue), "Generated mapping/parameterized LINQ failed.");
    var item = Graph.Node<Item>();
    var command = db.Cypher().Match(item).Where(item.Property(x => x.Name).Eq(privateValue))
        .Return(() => item.Value.Name).Limit(2).ToCommand();
    Require(command.Parameters.Values.Contains(privateValue) && !command.Text.Contains(privateValue), "Packaged Fluent compilation failed.");
    db.Add(new Item { Id = Guid.NewGuid(), Name = "tracked" });
    Require(db.ChangeTracker.Entries<Item>().Single().State == EntityState.Added, "Generated entity does not use EF tracking.");
}
// Exercise actual RDF and YAML dependency loading, rather than just typeof(ToolingType).
var compilation = OntologyCompiler.Compile(
    "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> . <urn:test:Item> a rdfs:Class .",
    "contractVersion: 1\nnamespace: Combined.Generated\ncontextName: CombinedContext\nentities: {}\nrelationships: {}\n");
Require(compilation.SourceFingerprint.Length == 64, "Packaged authoring inputs were not processed.");
Require(!compilation.Diagnostics.Any(x => x.Code is "InvalidTurtle" or "InvalidPolicyYaml"), "Packaged RDF/YAML processing failed.");
Require(probe.Calls == 0, "Offline combined consumer attempted transport.");
Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "GraphBridge.Generators"), "Analyzer loaded as a runtime dependency.");

var assemblies = new[] { typeof(DbContext).Assembly, typeof(IDriver).Assembly,
    typeof(GraphBridge.EntityFrameworkCore.FluentCypher.CypherCommand).Assembly, typeof(OntologyCompiler).Assembly };
var evidence = assemblies.Select(a => new { name = a.GetName().Name, assemblyVersion = a.GetName().Version!.ToString(),
    productVersion = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion }).ToArray();
var expected = new Dictionary<string, string> { ["Microsoft.EntityFrameworkCore"] = "10.0.12", ["Neo4j.Driver"] = "6.3.0" };
foreach (var entry in evidence.Where(e => expected.ContainsKey(e.name!)))
    Require(entry.productVersion.Split('+')[0] == expected[entry.name!], "Loaded dependency differs from the tested patch: " + entry.name);
if (args.Length > 1 || args.Length == 1 && args[0] != "--live") throw new ArgumentException("Only --live is supported.");
if (args.Length == 1) await CombinedLiveProbe.RunAsync();
Console.WriteLine(JsonSerializer.Serialize(new { status = "pass", driverCalls = probe.Calls, assemblies = evidence, live = args.Length == 1 }));

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

[GraphModel(typeof(Item), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
public static partial class CombinedMapping { }
[GraphNode("CombinedPackageItem", nameof(Id))]
public sealed class Item
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("name", true)] public string Name { get; set; } = "";
}
public sealed class CombinedContext(IDriver driver, string database = "offline") : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => CombinedMapping.Apply(modelBuilder);
}
public class RejectTransport : DispatchProxy
{
    public int Calls { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("Offline combined consumer attempted transport.");
    }
}
