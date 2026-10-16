using System.Text;
using GraphBridge.Tool;
using GraphBridge.Tooling.Generation;

namespace GraphBridge.ToolingTests;

public sealed class CommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graphbridge-tool-tests-" + Guid.NewGuid().ToString("N"));
    public CommandTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Publish_check_and_regenerate_preserve_bytes_and_protect_manual_edits()
    {
        var args = Inputs();
        Assert.Equal(0, Run(args));
        var directory = Path.Combine(_root, "generated");
        var before = Snapshot(directory);
        Assert.All(Directory.EnumerateFiles(directory), file => Assert.DoesNotContain('\r', File.ReadAllText(file)));
        Assert.Equal(0, Run([.. args, "--check"]));
        Assert.Equal(0, Run(args));
        Assert.Equal(before, Snapshot(directory));
        var entityFile = Path.Combine(directory, "Entities.g.cs");
        File.AppendAllText(entityFile, "// manual customization\n");
        var changed = Snapshot(directory);
        Assert.Equal(3, Run([.. args, "--check"]));
        Assert.Equal(2, Run(args));
        Assert.Equal(changed, Snapshot(directory));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".graphbridge-*"));
    }

    [Fact]
    public void Invalid_policy_and_missing_output_check_do_not_create_output()
    {
        var args = Inputs();
        Assert.Equal(3, Run([.. args, "--check"]));
        Assert.False(Directory.Exists(Path.Combine(_root, "generated")));
        File.WriteAllText(Path.Combine(_root, "policy.yaml"), "unknown: invalid\n");
        Assert.Equal(2, Run(args));
        Assert.False(Directory.Exists(Path.Combine(_root, "generated")));
    }

    [Fact]
    public void Publication_refuses_unrelated_files_and_unknown_files_inside_managed_output()
    {
        var artifacts = ArtifactTests.Generate();
        var directory = Path.Combine(_root, "generated");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user.txt"), "preserve me");
        Assert.Throws<IOException>(() => GraphArtifactDirectory.Publish(artifacts, directory));
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(directory, "user.txt")));
        File.Delete(Path.Combine(directory, "user.txt"));
        GraphArtifactDirectory.Publish(artifacts, directory);
        File.WriteAllText(Path.Combine(directory, "user.txt"), "preserve me too");
        var before = Snapshot(directory);
        Assert.Throws<IOException>(() => GraphArtifactDirectory.Publish(artifacts, directory));
        Assert.Equal(before, Snapshot(directory));
    }

    [Fact]
    public void Publication_replaces_recognized_unmodified_set_and_removes_obsolete_profile_file()
    {
        var args = Inputs();
        Assert.Equal(0, Run(args));
        var directory = Path.Combine(_root, "generated");
        Assert.True(File.Exists(Path.Combine(directory, "schema-community.cypher")));
        Assert.Equal(0, Run([.. args, "--target-edition", "enterprise"]));
        Assert.False(File.Exists(Path.Combine(directory, "schema-community.cypher")));
        Assert.True(File.Exists(Path.Combine(directory, "schema-enterprise.cypher")));
        Assert.Equal(0, Run([.. args, "--target-edition", "enterprise", "--check"]));
    }

    [Fact]
    public void Shacl_command_reports_real_conformance_and_violation()
    {
        var shapes = Path.Combine(_root, "shapes.ttl");
        var data = Path.Combine(_root, "data.ttl");
        File.WriteAllText(shapes, """
            @prefix ex: <https://example.org/> .
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            ex:Shape a sh:NodeShape; sh:targetClass ex:Person;
                sh:property [sh:path ex:name; sh:minCount 1] .
            """);
        File.WriteAllText(data, "<https://example.org/a> a <https://example.org/Person>; <https://example.org/name> \"Alice\" .");
        Assert.Equal(0, Run(["shacl", "--shapes", shapes, "--data", data]));
        File.WriteAllText(data, "<https://example.org/a> a <https://example.org/Person> .");
        Assert.Equal(2, Run(["shacl", "--shapes", shapes, "--data", data]));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("ontology", "--output")]
    [InlineData("ontology", "--check", "--check")]
    [InlineData("shacl", "--output", "x")]
    public void Invalid_command_returns_usage_failure_without_files(params string[] args)
    {
        Assert.Equal(2, Run(args));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    private string[] Inputs()
    {
        File.WriteAllText(Path.Combine(_root, "ontology.ttl"), SemanticFixture.Ontology, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "policy.yaml"), SemanticFixture.Policy, new UTF8Encoding(false));
        return ["ontology", "--ontology", Path.Combine(_root, "ontology.ttl"), "--policy", Path.Combine(_root, "policy.yaml"),
            "--output", Path.Combine(_root, "generated")];
    }
    private static int Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return GraphBridgeCommand.Run(args, output, error);
    }
    private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    public void Dispose()
    {
        // Delete only this test's explicitly created leaf files/directories; never recurse through links.
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        foreach (var file in Directory.EnumerateFiles(_root)) File.Delete(file);
        Directory.Delete(_root);
    }
}
