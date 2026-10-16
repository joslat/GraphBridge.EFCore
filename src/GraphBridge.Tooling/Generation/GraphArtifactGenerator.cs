using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Generation;

/// <summary>An explicit offline target declaration, not an observation of a live server.</summary>
public sealed class GraphArtifactOptions(string serverVersion = "2026.09.0", string edition = "community")
{
    public string ServerVersion { get; } = serverVersion;
    public string Edition { get; } = edition;
}

public sealed class GraphArtifactResult
{
    internal GraphArtifactResult(IEnumerable<GraphGeneratedFile> files, IEnumerable<GraphGenerationDiagnostic> diagnostics,
        string? fingerprint)
    {
        Files = Array.AsReadOnly(files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct().OrderBy(d => d.Subject, StringComparer.Ordinal)
            .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ThenBy(d => d.Severity).ToArray());
        Fingerprint = fingerprint;
    }
    public bool Success => !Diagnostics.Any(d => d.Severity == GraphGenerationSeverity.Error);
    public IReadOnlyList<GraphGeneratedFile> Files { get; }
    public IReadOnlyList<GraphGenerationDiagnostic> Diagnostics { get; }
    public string? Fingerprint { get; }
}

/// <summary>Compiles only source produced by the typed emitter, then observes its actual finalized EF model.</summary>
public static class GraphArtifactGenerator
{
    public static GraphArtifactResult Generate(GraphGenerationModel model, string sourceFingerprint,
        IEnumerable<GraphGenerationDiagnostic>? inputDiagnostics = null, GraphArtifactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        if (sourceFingerprint.Length != 64 || sourceFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Source fingerprint must be SHA-256 hexadecimal.", nameof(sourceFingerprint));
        options ??= new();
        var diagnostics = inputDiagnostics?.ToList() ?? [];
        var generated = GraphCSharpEmitter.Generate(model);
        diagnostics.AddRange(generated.Diagnostics);
        if (diagnostics.Any(d => d.Severity == GraphGenerationSeverity.Error)) return new([], diagnostics, null);
        if (options.Edition is not ("community" or "enterprise"))
            return Failure("GBART001", "target", "Target edition must be community or enterprise.", diagnostics);
        var capabilities = GraphSchemaCapabilities.Detect(options.ServerVersion, options.Edition, ["5", "25"]);
        if (capabilities.NodeUniqueness != GraphSchemaCapabilitySupport.Supported)
            return Failure("GBART002", "target", "Target version has no recognized uniqueness capability; no deployment proposal is emitted.", diagnostics);
        if (model.IdentifiedEdges.Count > 0 && capabilities.RelationshipUniqueness != GraphSchemaCapabilitySupport.Supported)
            return Failure("GBART002", "target", "Identified edges require recognized relationship uniqueness support.", diagnostics);

        GraphSchemaManifest manifest;
        try { manifest = CompileManifest(generated); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var primary = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            return Failure("GBART003", "generated-model", primary.Message, diagnostics);
        }
        var comparison = GraphSchemaComparer.Compare(manifest, capabilities, new GraphSchemaObservation([], []));
        var files = generated.Files.ToList();
        files.Add(new("graphbridge.binding.json", manifest.CanonicalJson + "\n"));
        files.Add(new("graphbridge.binding.sha256", manifest.Fingerprint + "\n"));
        var schema = "// Generated proposal for declared " + options.ServerVersion + " " + options.Edition + ".\n"
            + "// Review before explicit deployment. No live capabilities or graph data were observed.\n"
            + string.Join("\n", comparison.Proposals.Select(p => p.Cypher.TrimEnd(';') + ";")) + "\n";
        files.Add(new("schema-" + options.Edition + ".cypher", schema));
        files.Add(new("generation-report.md", Report(model, sourceFingerprint, manifest, options, diagnostics, comparison)));
        return new(files, diagnostics, manifest.Fingerprint);
    }

    private static GraphSchemaManifest CompileManifest(GraphSourceGenerationResult generated)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", generated.Files.Select(f => f.Content)))));
        var references = CompilationReferences();
        var compilation = CSharpCompilation.Create("GraphBridge.Generated." + sourceHash,
            generated.Files.Select(file => CSharpSyntaxTree.ParseText(file.Content,
                new CSharpParseOptions(LanguageVersion.CSharp14), file.RelativePath, Encoding.UTF8)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable, deterministic: true));
        using var assemblyBytes = new MemoryStream();
        var emitted = compilation.Emit(assemblyBytes);
        var errors = emitted.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).ToArray();
        if (!emitted.Success || errors.Length > 0)
            throw new InvalidOperationException("Generated source must compile without warnings: "
                + string.Join("; ", errors.Select(d => d.ToString()).Order(StringComparer.Ordinal)));
        assemblyBytes.Position = 0;
        var loadContext = new AssemblyLoadContext("GraphBridge.Generated." + sourceHash, isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromStream(assemblyBytes);
            var contextType = assembly.GetType(generated.ContextTypeName!, throwOnError: true)!;
            var driver = DispatchProxy.Create<IDriver, OfflineDriver>();
            var contextOptions = new DbContextOptionsBuilder().UseNeo4j(driver)
                .EnableServiceProviderCaching(false).Options;
            using var context = (DbContext)Activator.CreateInstance(contextType, contextOptions)!;
            return context.Database.GetGraphSchemaManifest();
        }
        finally { loadContext.Unload(); }
    }

    private static IEnumerable<MetadataReference> CompilationReferences()
    {
        // TPA includes application dependencies, including an already compiled version of the model being regenerated.
        // For the supported framework-dependent tools, import shared-runtime facades and the provider/EF/driver closure.
        // Self-contained/single-file tooling deployments require a separate reference resolver and are not verified.
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(Path.GetDirectoryName(path), runtimeDirectory, StringComparison.OrdinalIgnoreCase))
                paths.Add(path);
        var pending = new Stack<Assembly>([typeof(DbContext).Assembly, typeof(GraphSchemaManifest).Assembly, typeof(IDriver).Assembly]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var assembly))
        {
            if (!visited.Add(assembly.FullName!)) continue;
            paths.Add(assembly.Location);
            foreach (var dependency in assembly.GetReferencedAssemblies()) pending.Push(Assembly.Load(dependency));
        }
        return paths.Order(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
    }

    private static GraphArtifactResult Failure(string code, string subject, string message,
        List<GraphGenerationDiagnostic> diagnostics)
    {
        diagnostics.Add(new(code, GraphGenerationSeverity.Error, subject, message));
        return new([], diagnostics, null);
    }

    private static string Report(GraphGenerationModel model, string sourceFingerprint, GraphSchemaManifest manifest,
        GraphArtifactOptions options, IEnumerable<GraphGenerationDiagnostic> diagnostics, GraphSchemaComparison comparison)
    {
        var text = new StringBuilder("# GraphBridge generation report\n\n");
        text.Append("Source SHA-256: `").Append(sourceFingerprint.ToLowerInvariant()).Append("`.\n\n")
            .Append("Finalized EF storage fingerprint: `").Append(manifest.Fingerprint).Append("`.\n\n")
            .Append("Declared target: ").Append(Safe(options.ServerVersion)).Append(' ').Append(Safe(options.Edition))
            .Append(". This is an offline declaration, not live capability detection.\n\n")
            .Append("Generated model: ").Append(model.Nodes.Count).Append(" node types and ")
            .Append(model.IdentifiedEdges.Count).Append(" identified relationship types.\n\n")
            .Append("The compiled generated DbContext and its finalized EF IModel are the source of the storage manifest. ")
            .Append("Source provenance is separate from the storage fingerprint. Application partial customizations are not compiled by this tool; they can change the application's runtime model and fingerprint.\n\n")
            .Append("RDFS domain/range describe inference, not closed-world requiredness or uniqueness. ")
            .Append("The explicit persistence policy supplies keys, property types, conversions, direction and deletion. ")
            .Append("No OWL reasoning or imports run. Identified relationship payloads and parallel identities are persistence extensions to RDF triple-set semantics. ")
            .Append("SHACL validation is separate and does not become runtime EF enforcement.\n\n")
            .Append("Date-only values, when requested, use the policy's explicit invariant ISO string conversion; no native Neo4j temporal mapping is implied. ")
            .Append("Enterprise proposals require that declared edition; Graph Type replacement output is not generated. ")
            .Append("Schema text is a reviewable proposal and generation performs no deployment or data migration.\n\n")
            .Append("## Diagnostics\n\n| Severity | Code | Subject | Message |\n|---|---|---|---|\n");
        foreach (var d in diagnostics.Distinct().OrderBy(d => d.Subject, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ThenBy(d => d.Severity))
            text.Append("| ").Append(d.Severity).Append(" | ").Append(Safe(d.Code)).Append(" | ").Append(Safe(d.Subject)).Append(" | ").Append(Safe(d.Message)).Append(" |\n");
        text.Append("\n## Storage caveats\n\n");
        foreach (var caveat in manifest.Caveats) text.Append("- ").Append(Safe(caveat.ToString()!)).Append('\n');
        text.Append("\n## Empty-target comparison\n\nMissing constraints are expected before explicit deployment.\n\n");
        foreach (var issue in comparison.Issues) text.Append("- ").Append(Safe(issue.ToString()!)).Append('\n');
        return text.ToString();
    }

    private static string Safe(string value) => System.Net.WebUtility.HtmlEncode(value).Replace("|", "&#124;", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>Any attempted transport during offline model construction is an error.</summary>
    public class OfflineDriver : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Offline generation attempted driver access: " + targetMethod?.Name);
    }
}
