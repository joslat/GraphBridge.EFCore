using System.Text;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Semantics;
using GraphBridge.Tooling.Scaffolding;
using Neo4j.Driver;

namespace GraphBridge.Tool;

public static class Program
{
    public static Task<int> Main(string[] args) => GraphBridgeCommand.RunAsync(args, Console.Out, Console.Error);
}

public static class GraphBridgeCommand
{
    /// <summary>Async entry point for explicit live inspection; other commands remain entirely offline.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args[0] != "inspect") return Run(args, output, error);
        try
        {
            var options = ParseOptions(args, ["--output", "--labels", "--relationship-types"]);
            var destination = Path.GetFullPath(Required(options, "--output"));
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Snapshot output already exists; inspection never overwrites an existing path.");
            var parent = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(parent)) throw new IOException("Snapshot output parent directory must already exist.");
            NoLinks(parent);
            environment ??= Environment.GetEnvironmentVariable;
            string Setting(string name)
            {
                var value = environment(name);
                return !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Required environment setting: " + name);
            }
            var uriText = Setting("GRAPHBRIDGE_URI");
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("bolt" or "bolt+s" or "bolt+ssc" or "neo4j" or "neo4j+s" or "neo4j+ssc")
                || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("" or "/"))
                throw new ArgumentException("GRAPHBRIDGE_URI must be an explicit Neo4j URI without embedded credentials, path, query, or fragment.");
            var database = Setting("GRAPHBRIDGE_DATABASE");
            if (database.Length > 256 || database.Any(char.IsControl)) throw new ArgumentException("Invalid GRAPHBRIDGE_DATABASE.");
            var username = Setting("GRAPHBRIDGE_USERNAME");
            var password = Setting("GRAPHBRIDGE_PASSWORD");
            string[]? Selection(string name) => options.TryGetValue(name, out var value)
                ? value.Split(',').Select(token => token.Trim()).ToArray() : null;
            var inspectionOptions = new GraphInspectionOptions(labels: Selection("--labels"), relationshipTypes: Selection("--relationship-types"));
            cancellationToken.ThrowIfCancellationRequested();
            await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(username, password),
                builder => builder.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
            var snapshot = await GraphDatabaseInspector.InspectAsync(driver, database, inspectionOptions, cancellationToken).ConfigureAwait(false);
            var content = GraphScaffoldJson.WriteSnapshot(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            var stage = Path.Combine(parent, ".graphbridge-snapshot-" + Guid.NewGuid().ToString("N") + ".tmp");
            NoLinks(parent);
            var created = false;
            try
            {
                await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                NoLinks(parent);
                File.Move(stage, destination, overwrite: false);
            }
            finally { if (created && File.Exists(stage)) File.Delete(stage); }
            output.WriteLine("Inspection snapshot saved. Review bounded observations before creating a mapping policy.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Inspection canceled; no database writes were requested.");
            return 2;
        }
        catch (Exception exception) when (exception is Neo4jException)
        {
            // Server messages may contain addresses or application data; never echo them or credentials.
            error.WriteLine("Neo4j inspection failed. Check connectivity, selected database and read permissions.");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args is ["--help"] or ["help"])
        {
            output.WriteLine("graphbridge ontology --ontology FILE --policy FILE [--shapes FILE] --output DIRECTORY [--check] [--target-version VERSION] [--target-edition community|enterprise]");
            output.WriteLine("graphbridge shacl --shapes FILE --data FILE");
            output.WriteLine("graphbridge inspect --output SNAPSHOT.json [--labels LABEL,...] [--relationship-types TYPE,...]");
            output.WriteLine("  Requires GRAPHBRIDGE_URI, GRAPHBRIDGE_DATABASE, GRAPHBRIDGE_USERNAME and GRAPHBRIDGE_PASSWORD; read-only, bounded inspection.");
            output.WriteLine("graphbridge scaffold --snapshot SNAPSHOT.json --policy POLICY.json --output DIRECTORY [--check]");
            output.WriteLine("graphbridge scaffold-review --snapshot CURRENT.json --policy CURRENT_POLICY.json --baseline-snapshot BASELINE.json --baseline-policy BASELINE_POLICY.json");
            output.WriteLine("  Offline JSON review on stdout, including invalid proposals; no generated files or schema changes.");
            return 0;
        }
        try
        {
            var command = args[0];
            var allowed = command switch
            {
                "ontology" => new[] { "--ontology", "--policy", "--shapes", "--output", "--check", "--target-version", "--target-edition" },
                "shacl" => ["--shapes", "--data"],
                "scaffold" => ["--snapshot", "--policy", "--output", "--check"],
                "scaffold-review" => ["--snapshot", "--policy", "--baseline-snapshot", "--baseline-policy"],
                "inspect" => throw new ArgumentException("Live inspection requires the asynchronous RunAsync entry point."),
                _ => throw new ArgumentException("Unknown command; use --help for supported commands.")
            };
            var options = ParseOptions(args, allowed);
            string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("Required option: " + name);
            if (command == "shacl")
            {
                var validated = ShaclValidator.Validate(Read(Required("--shapes")), Read(Required("--data")));
                Diagnostics(validated.Diagnostics, error);
                output.WriteLine(validated.Validated ? "SHACL conforms: " + validated.Conforms : "SHACL validation did not run.");
                return validated.Validated && validated.Conforms == true ? 0 : 2;
            }
            if (command == "scaffold-review")
            {
                var review = GraphScaffoldReview.Compare(
                    GraphScaffoldJson.ReadSnapshot(Read(Required("--baseline-snapshot"))),
                    GraphScaffoldJson.ReadPolicy(Read(Required("--baseline-policy"))),
                    GraphScaffoldJson.ReadSnapshot(Read(Required("--snapshot"))),
                    GraphScaffoldJson.ReadPolicy(Read(Required("--policy"))));
                output.Write(review.ToJson());
                return review.Success ? 0 : 2;
            }
            if (command == "scaffold")
            {
                var snapshot = GraphScaffoldJson.ReadSnapshot(Read(Required("--snapshot")));
                var reviewedPolicy = GraphScaffoldJson.ReadPolicy(Read(Required("--policy")));
                var scaffold = GraphScaffoldCompiler.Compile(snapshot, reviewedPolicy);
                var scaffoldArtifacts = GraphScaffoldArtifacts.Generate(scaffold, snapshot.Capabilities);
                Diagnostics(scaffoldArtifacts.Diagnostics, error);
                if (!scaffoldArtifacts.Success) return 2;
                var directory = Required("--output");
                if (options.ContainsKey("--check"))
                {
                    if (!GraphArtifactDirectory.Matches(scaffoldArtifacts, directory))
                    {
                        error.WriteLine("Scaffold artifacts are missing or differ; --check made no changes.");
                        return 3;
                    }
                    output.WriteLine("Scaffold artifacts match.");
                    return 0;
                }
                GraphArtifactDirectory.Publish(scaffoldArtifacts, directory);
                output.WriteLine(scaffoldArtifacts.Fingerprint is null
                    ? "Generated read-only DTO proposals and inference report. No tracked EF model or storage fingerprint was produced."
                    : "Generated reviewed model and inference report. Storage fingerprint: " + scaffoldArtifacts.Fingerprint);
                return 0;
            }
            var ontology = Read(Required("--ontology"));
            var policy = Read(Required("--policy"));
            var destination = Required("--output");
            var shapes = options.TryGetValue("--shapes", out var shapesPath) ? Read(shapesPath) : null;
            var compilation = OntologyCompiler.Compile(ontology, policy, shapes);
            if (!compilation.Success || compilation.Model is null)
            {
                Diagnostics(compilation.Diagnostics, error);
                return 2;
            }
            var artifacts = GraphArtifactGenerator.Generate(compilation.Model, compilation.SourceFingerprint,
                compilation.Diagnostics, new(options.GetValueOrDefault("--target-version", "2026.09.0"),
                    options.GetValueOrDefault("--target-edition", "community")));
            Diagnostics(artifacts.Diagnostics, error);
            if (!artifacts.Success) return 2;
            if (options.ContainsKey("--check"))
            {
                if (!GraphArtifactDirectory.Matches(artifacts, destination))
                {
                    error.WriteLine("Generated artifacts are missing or differ; --check made no changes.");
                    return 3;
                }
                output.WriteLine("Generated artifacts match. Storage fingerprint: " + artifacts.Fingerprint);
                return 0;
            }
            GraphArtifactDirectory.Publish(artifacts, destination);
            output.WriteLine("Generated " + artifacts.Files.Count + " artifacts. Storage fingerprint: " + artifacts.Fingerprint);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args, string[] allowed)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (!allowed.Contains(name, StringComparer.Ordinal) || options.ContainsKey(name))
                throw new ArgumentException("Unknown or duplicate option; use --help for supported options.");
            if (name == "--check") { options.Add(name, "true"); continue; }
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Missing value for " + name);
            options.Add(name, args[index]);
        }
        return options;
    }
    private static string Required(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Required option: " + name);
    private static void NoLinks(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Snapshot output paths cannot traverse filesystem links.");
    }

    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024) throw new IOException("Input exceeds the 4 MiB file limit.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
    private static void Diagnostics(IEnumerable<GraphGenerationDiagnostic> diagnostics, TextWriter error)
    {
        foreach (var diagnostic in diagnostics)
            error.WriteLine($"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Subject}: {diagnostic.Message}");
    }
}
