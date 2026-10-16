using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;

namespace GraphBridge.Tooling.Scaffolding;

/// <summary>Builds publishable DTO proposals or a verified finalized EF model from a reviewed scaffold compilation.</summary>
public static class GraphScaffoldArtifacts
{
    public static GraphArtifactResult Generate(GraphScaffoldCompilationResult compilation, GraphSchemaCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!compilation.Success) return new([], compilation.Diagnostics, null);
        if (compilation.Model is null)
            return new(compilation.Files, compilation.Diagnostics, null);
        var artifacts = GraphArtifactGenerator.Generate(compilation.Model, compilation.SourceFingerprint,
            compilation.Diagnostics, new(capabilities.ServerVersion, capabilities.Edition.ToLowerInvariant()));
        if (!artifacts.Success) return artifacts;
        var files = artifacts.Files.Where(f => f.RelativePath != "generation-report.md").Concat(compilation.Files).ToList();
        files.Add(new("generation-report.md", "# GraphBridge database-first generation report\n\n"
            + "Source SHA-256: `" + compilation.SourceFingerprint + "`.\n\n"
            + "Finalized EF storage fingerprint: `" + artifacts.Fingerprint + "`.\n\n"
            + "Tracked types were compiled from the reviewed mapping policy using the real EF model. "
            + "Unmapped shapes remain DTO proposals. See the inference report for observation limits and assumptions. "
            + "No uniqueness or requiredness is inferred from sampled values. No element IDs become keys.\n\n"
            + "Schema files are empty-target proposals for the snapshot's declared server version and edition. "
            + "They are not a migration plan for the inspected database. Generation never connects or deploys. "
            + "Partial application customizations can change the runtime model and fingerprint.\n"));
        return new(files, artifacts.Diagnostics, artifacts.Fingerprint);
    }
}
