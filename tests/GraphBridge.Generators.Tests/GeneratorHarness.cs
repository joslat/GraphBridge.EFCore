using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Generators.Tests;

internal static class GeneratorHarness
{
    internal static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(CreateReferences);
    internal const string Minimal = """
        #nullable enable
        using GraphBridge.Generated;
        namespace Fixture;
        [GraphModel(typeof(Item), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)] public static partial class Mapping { }
        [GraphNode("GeneratorItem", nameof(Id))]
        public sealed class Item
        {
            [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public System.Guid Id { get; set; }
            [GraphProperty("title", false)] public string? Title { get; set; }
        }
        """;
    internal static CSharpCompilation Compilation(string source) => CSharpCompilation.Create("GeneratorTest_" + Guid.NewGuid().ToString("N"),
        [CSharpSyntaxTree.ParseText(source, ParseOptions, "Model.cs", Encoding.UTF8)], References.Value,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
            deterministic: true));
    internal static GeneratorDriver Driver(bool track = false) => CSharpGeneratorDriver.Create(
        [new GraphMappingGenerator().AsSourceGenerator()], parseOptions: ParseOptions,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: track));
    internal static (GeneratorDriver Driver, Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var driver = Driver().RunGeneratorsAndUpdateCompilation(Compilation(source), out var output, out var diagnostics);
        return (driver, output, diagnostics);
    }
    internal static string MappingSource(GeneratorDriver driver) => Assert.Single(driver.GetRunResult().Results.Single().GeneratedSources,
        source => source.HintName.EndsWith(".GraphBridge.g.cs", StringComparison.Ordinal)).SourceText.ToString();
    internal static void AssertClean(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.Empty(diagnostics);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error));
    }
    internal static void WithAssembly(Compilation compilation, Action<Assembly> inspect)
    {
        using var bytes = new MemoryStream();
        var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        bytes.Position = 0;
        var load = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try { inspect(load.LoadFromStream(bytes)); }
        finally { load.Unload(); }
    }
    internal static DbContextOptions Options() => new DbContextOptionsBuilder()
        .UseNeo4j(DispatchProxy.Create<IDriver, NoTransport>()).EnableServiceProviderCaching(false).Options;
    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(Path.GetDirectoryName(path), runtime, StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        var pending = new Stack<Assembly>([typeof(DbContext).Assembly, typeof(GraphSchemaManifest).Assembly, typeof(IDriver).Assembly]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var assembly))
        {
            if (!seen.Add(assembly.FullName!)) continue;
            paths.Add(assembly.Location);
            foreach (var dependency in assembly.GetReferencedAssemblies()) pending.Push(Assembly.Load(dependency));
        }
        return paths.Order(StringComparer.Ordinal).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }
}

public class NoTransport : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("Generator parity must never access the driver: " + targetMethod?.Name);
}
