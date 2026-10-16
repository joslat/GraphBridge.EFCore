using GraphBridge.Tool;
using GraphBridge.Tooling.Scaffolding;

namespace GraphBridge.ToolingTests;

public sealed class ScaffoldCommandTests : IDisposable
{
    [Theory]
    [InlineData("--password=secret-password")]
    [InlineData("--uri=bolt://user:secret-password@localhost")]
    [InlineData("secret-password")]
    public async Task Unknown_argument_diagnostics_never_echo_possible_credentials(string argument)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await GraphBridgeCommand.RunAsync(["inspect", argument], output, error));
        Assert.DoesNotContain("secret-password", error.ToString());
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graphbridge-scaffold-tests-" + Guid.NewGuid().ToString("N"));
    public ScaffoldCommandTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Offline_review_emits_JSON_for_valid_and_conflicting_policies_without_writing_files(bool conflict)
    {
        _ = Inputs(ScaffoldJsonTests.Policy());
        var snapshot = Path.Combine(_root, "snapshot.json");
        var policy = Path.Combine(_root, "policy.json");
        var current = Path.Combine(_root, "current.json");
        File.WriteAllText(current, conflict ? File.ReadAllText(policy).Replace("\"approveRequiredKey\": true", "\"approveRequiredKey\": false", StringComparison.Ordinal) : File.ReadAllText(policy));
        var originals = Directory.GetFiles(_root).ToDictionary(p => p, File.ReadAllText);
        var result = Run(["scaffold-review", "--snapshot", snapshot, "--policy", current, "--baseline-snapshot", snapshot, "--baseline-policy", policy]);
        Assert.Equal(conflict ? 2 : 0, result.Code);
        using var parsed = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.Equal(!conflict, parsed.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(conflict ? System.Text.Json.JsonValueKind.Null : System.Text.Json.JsonValueKind.True, parsed.RootElement.GetProperty("modelsEquivalent").ValueKind);
        Assert.Empty(result.Error);
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.Equal(originals.Count, Directory.GetFiles(_root).Length);
        Assert.All(originals, pair => Assert.Equal(pair.Value, File.ReadAllText(pair.Key)));
    }

    [Fact]
    public void Review_requires_explicit_baseline_policy_and_rejects_publish_options()
    {
        _ = Inputs(ScaffoldJsonTests.Policy());
        var args = new[] { "scaffold-review", "--snapshot", Path.Combine(_root, "snapshot.json"), "--policy", Path.Combine(_root, "policy.json"), "--baseline-snapshot", Path.Combine(_root, "snapshot.json") };
        Assert.Equal(2, Run(args).Code);
        Assert.Equal(2, Run([.. args, "--output", "not-created"]).Code);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Offline_dto_proposal_publishes_checks_and_protects_manual_edits_without_tracking_claim()
    {
        var args = Inputs(new("Example", "MoviesContext"));
        var result = Run(args);
        Assert.Equal(0, result.Code);
        Assert.Contains("No tracked EF model", result.Output);
        Assert.True(File.Exists(Path.Combine(_root, "generated", "ScaffoldReadOnlyDtos.g.cs")));
        Assert.True(File.Exists(Path.Combine(_root, "generated", "inference-report.md")));
        Assert.False(File.Exists(Path.Combine(_root, "generated", "graphbridge.binding.sha256")));
        Assert.Equal(0, Run([.. args, "--check"]).Code);
        var dto = Path.Combine(_root, "generated", "ScaffoldReadOnlyDtos.g.cs");
        File.AppendAllText(dto, "// manual edit\n");
        Assert.Equal(3, Run([.. args, "--check"]).Code);
        Assert.Equal(2, Run(args).Code);
        Assert.EndsWith("// manual edit\n", File.ReadAllText(dto));
    }

    [Fact]
    public void Reviewed_tracked_policy_emits_actual_manifest_alongside_dto_and_inference_report()
    {
        var args = Inputs(ScaffoldJsonTests.Policy());
        var result = Run(args);
        Assert.True(result.Code == 0, result.Error);
        Assert.Contains("Storage fingerprint:", result.Output);
        var directory = Path.Combine(_root, "generated");
        Assert.True(File.Exists(Path.Combine(directory, "graphbridge.binding.json")));
        Assert.True(File.Exists(Path.Combine(directory, "ScaffoldReadOnlyDtos.g.cs")));
        Assert.True(File.Exists(Path.Combine(directory, "inference-report.md")));
        Assert.Contains("database-first", File.ReadAllText(Path.Combine(directory, "generation-report.md")));
        Assert.DoesNotContain("RDFS", File.ReadAllText(Path.Combine(directory, "generation-report.md")));
        Assert.All(Directory.EnumerateFiles(directory), path => Assert.DoesNotContain('\r', File.ReadAllText(path)));
        Assert.Equal(0, Run([.. args, "--check"]).Code);
    }

    [Fact]
    public void Missing_check_and_invalid_tracking_policy_never_publish()
    {
        var args = Inputs(new("Example", "MoviesContext"));
        Assert.Equal(3, Run([.. args, "--check"]).Code);
        Assert.False(Directory.Exists(Path.Combine(_root, "generated")));
        File.WriteAllText(Path.Combine(_root, "policy.json"), GraphScaffoldJson.WritePolicy(new("Example", "MoviesContext",
            [new("Person", "Person", ["missing"], false)])));
        Assert.Equal(2, Run(args).Code);
        Assert.False(Directory.Exists(Path.Combine(_root, "generated")));
    }

    [Theory]
    [InlineData("GRAPHBRIDGE_URI")]
    [InlineData("GRAPHBRIDGE_DATABASE")]
    [InlineData("GRAPHBRIDGE_USERNAME")]
    [InlineData("GRAPHBRIDGE_PASSWORD")]
    public async Task Inspect_requires_every_explicit_connection_setting_before_driver_creation(string missing)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var values = Settings();
        values.Remove(missing);
        var code = await GraphBridgeCommand.RunAsync(["inspect", "--output", Path.Combine(_root, "snapshot.json")], output, error,
            name => values.GetValueOrDefault(name));
        Assert.Equal(2, code);
        Assert.Contains(missing, error.ToString());
        Assert.DoesNotContain("secret-password", error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("bolt://user:secret-password@localhost:7687")]
    [InlineData("http://localhost:7687")]
    [InlineData("bolt://localhost:7687/path")]
    [InlineData("bolt://localhost:7687?password=secret-password")]
    public async Task Inspect_rejects_ambiguous_or_embedded_credentials_without_echoing_values(string uri)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var values = Settings();
        values["GRAPHBRIDGE_URI"] = uri;
        Assert.Equal(2, await GraphBridgeCommand.RunAsync(["inspect", "--output", Path.Combine(_root, "snapshot.json")], output, error,
            name => values.GetValueOrDefault(name)));
        Assert.DoesNotContain("secret-password", error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Inspect_cancellation_and_existing_output_fail_before_network_or_replacement()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new[] { "inspect", "--output", Path.Combine(_root, "snapshot.json") };
        var values = Settings();
        Assert.Equal(2, await GraphBridgeCommand.RunAsync(args, output, error, name => values.GetValueOrDefault(name), new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        File.WriteAllText(args[2], "preserve me");
        Assert.Equal(2, await GraphBridgeCommand.RunAsync(args, output, error, _ => throw new Exception("Settings must not be read.")));
        Assert.Equal("preserve me", File.ReadAllText(args[2]));
    }

    [Fact]
    public async Task Async_entry_delegates_offline_work_without_reading_connection_environment()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await GraphBridgeCommand.RunAsync(Inputs(new("Example", "MoviesContext")), output, error,
            _ => throw new Exception("Offline command must not read connection settings.")));
    }

    [Fact]
    public async Task Password_command_option_is_rejected_without_echoing_its_value()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await GraphBridgeCommand.RunAsync(["inspect", "--password", "secret-password"], output, error));
        Assert.DoesNotContain("secret-password", error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    private string[] Inputs(GraphScaffoldPolicy policy)
    {
        var source = ScaffoldJsonTests.Snapshot();
        var snapshot = new GraphScaffoldSnapshot(source.Capabilities,
            new([.. source.Schema.Constraints, new("person_id", "UNIQUENESS", "NODE", ["Person"], ["id"])], source.Schema.Indexes),
            source.Labels, source.RelationshipTypes, source.Nodes, source.Relationships, source.SampleLimit);
        File.WriteAllText(Path.Combine(_root, "snapshot.json"), GraphScaffoldJson.WriteSnapshot(snapshot));
        File.WriteAllText(Path.Combine(_root, "policy.json"), GraphScaffoldJson.WritePolicy(policy));
        return ["scaffold", "--snapshot", Path.Combine(_root, "snapshot.json"), "--policy", Path.Combine(_root, "policy.json"), "--output", Path.Combine(_root, "generated")];
    }
    private static Dictionary<string, string> Settings() => new()
    {
        ["GRAPHBRIDGE_URI"] = "bolt://127.0.0.1:1", ["GRAPHBRIDGE_DATABASE"] = "neo4j",
        ["GRAPHBRIDGE_USERNAME"] = "neo4j", ["GRAPHBRIDGE_PASSWORD"] = "secret-password"
    };
    private static (int Code, string Output, string Error) Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = GraphBridgeCommand.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
    public void Dispose()
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        foreach (var file in Directory.EnumerateFiles(_root)) File.Delete(file);
        Directory.Delete(_root);
    }
}
