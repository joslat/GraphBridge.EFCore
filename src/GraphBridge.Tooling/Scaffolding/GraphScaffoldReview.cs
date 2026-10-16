using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.Tooling.Generation;

namespace GraphBridge.Tooling.Scaffolding;

public sealed record GraphScaffoldReviewDiagnostic(string Code, string Severity, string Subject, string Message, string Action);
public sealed record GraphScaffoldReviewFile(string Path, string Sha256);
public sealed record GraphScaffoldReviewSide(bool Success, bool RoundTripVerified, string SourceFingerprint,
    string? ModelFingerprint, IReadOnlyList<GraphScaffoldReviewDiagnostic> Diagnostics, IReadOnlyList<GraphScaffoldReviewFile> Files);
/// <summary>Before/After contain JSON values; null means an absent path, not a JSON null value.</summary>
public sealed record GraphScaffoldReviewChange(string Path, string Kind, string? Before, string? After);

public sealed class GraphScaffoldReviewResult
{
    internal GraphScaffoldReviewResult(GraphScaffoldReviewSide baseline, GraphScaffoldReviewSide current,
        IReadOnlyList<GraphScaffoldReviewChange> observations, IReadOnlyList<GraphScaffoldReviewChange>? modelChanges,
        IReadOnlyList<GraphScaffoldReviewChange> artifactChanges)
        => (Baseline, Current, ObservationChanges, ModelChanges, ArtifactChanges) = (baseline, current, observations, modelChanges, artifactChanges);
    public int FormatVersion => 1;
    public string Scope => "Offline review of bounded observations and explicit policies; not a migration plan or proof of whole-database equivalence.";
    public GraphScaffoldReviewSide Baseline { get; }
    public GraphScaffoldReviewSide Current { get; }
    public bool Success => Baseline.Success && Current.Success && Baseline.RoundTripVerified && Current.RoundTripVerified;
    /// <summary>Null when either side has no successfully compiled tracked EF model.</summary>
    public bool? ModelsEquivalent => ModelChanges is null ? null : ModelChanges.Count == 0;
    public IReadOnlyList<GraphScaffoldReviewChange> ObservationChanges { get; }
    public IReadOnlyList<GraphScaffoldReviewChange>? ModelChanges { get; }
    public IReadOnlyList<GraphScaffoldReviewChange> ArtifactChanges { get; }
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n" }) + "\n";
}

/// <summary>Compiles both reviewed proposals offline and compares their actual finalized EF storage manifests.</summary>
public static class GraphScaffoldReview
{
    public static GraphScaffoldReviewResult Compare(GraphScaffoldSnapshot baseline, GraphScaffoldPolicy baselinePolicy,
        GraphScaffoldSnapshot current, GraphScaffoldPolicy currentPolicy)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baselinePolicy);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        var before = Build(baseline, baselinePolicy);
        var after = Build(current, currentPolicy);
        return new(before.Side, after.Side, Diff(before.Snapshot, after.Snapshot),
            before.Manifest is not null && after.Manifest is not null ? Diff(before.Manifest, after.Manifest) : null,
            Diff(JsonSerializer.Serialize(before.Side.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal)),
                JsonSerializer.Serialize(after.Side.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal))));
    }

    private static (GraphScaffoldReviewSide Side, string Snapshot, string? Manifest) Build(GraphScaffoldSnapshot snapshot, GraphScaffoldPolicy policy)
    {
        // Strict interchange bounds also apply to callers constructing snapshots/policies directly.
        var snapshotJson = GraphScaffoldJson.WriteSnapshot(snapshot);
        var policyJson = GraphScaffoldJson.WritePolicy(policy);
        var compilation = GraphScaffoldCompiler.Compile(snapshot, policy);
        var artifacts = GraphScaffoldArtifacts.Generate(compilation, snapshot.Capabilities);
        var repeatedSnapshot = GraphScaffoldJson.ReadSnapshot(snapshotJson);
        var repeated = GraphScaffoldCompiler.Compile(repeatedSnapshot, GraphScaffoldJson.ReadPolicy(policyJson));
        var repeatedArtifacts = GraphScaffoldArtifacts.Generate(repeated, repeatedSnapshot.Capabilities);
        var roundTrip = compilation.SourceFingerprint == repeated.SourceFingerprint
            && compilation.Files.SequenceEqual(repeated.Files) && artifacts.Files.SequenceEqual(repeatedArtifacts.Files)
            && artifacts.Fingerprint == repeatedArtifacts.Fingerprint && artifacts.Diagnostics.SequenceEqual(repeatedArtifacts.Diagnostics);
        var diagnostics = artifacts.Diagnostics.Select(d => new GraphScaffoldReviewDiagnostic(d.Code, d.Severity.ToString(), d.Subject, d.Message, Action(d))).ToList();
        if (!roundTrip) diagnostics.Add(new("RoundTripMismatch", "Error", "proposal",
            "Snapshot/policy serialization changed generated output or diagnostics.", "Do not accept this proposal; retain the input pair and report a deterministic-generation failure."));
        var files = artifacts.Files.Select(f => new GraphScaffoldReviewFile(f.RelativePath,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Content))).ToLowerInvariant())).ToArray();
        return (new(artifacts.Success && roundTrip, roundTrip, compilation.SourceFingerprint, artifacts.Fingerprint,
                diagnostics.AsReadOnly(), Array.AsReadOnly(files)), snapshotJson,
            artifacts.Success ? artifacts.Files.SingleOrDefault(f => f.RelativePath == "graphbridge.binding.json")?.Content : null);
    }

    private static string Action(GraphGenerationDiagnostic diagnostic) => diagnostic.Code switch
    {
        "MissingUniqueConstraint" or "KeyExistenceUnproven" or "KeyReviewRequired" or "ObservedMissingKey"
            => "Review application identity, existing constraints and required data separately. Keep DTO-only access until the tracked-key contract is justified; sampling cannot prove uniqueness/existence.",
        "MixedPropertyTypes" or "PropertyConversionRequired" or "IncompatibleStoredType" or "OpaquePayload"
            => "Review every observed kind. Use a compatible explicit mapping or ignore this tracked property and retain its DTO payload; converters do not migrate stored values.",
        "ConflictingEndpoints" or "UnresolvedEndpoint" or "OverlappingTrackedLabels"
            => "Review directed endpoint labels and overlapping node memberships. Correct the policy or keep the ambiguous shape in DTOs; do not discard conflicting observations.",
        "UnknownCardinality" or "ObservedParallelEdges" or "ReadOnlyRelationship"
            => "Retain explicit identified-edge or DTO semantics. Do not infer association uniqueness or navigation multiplicity from bounded samples.",
        _ => diagnostic.Message
    };

    private static IReadOnlyList<GraphScaffoldReviewChange> Diff(string before, string after)
    {
        using var leftDocument = JsonDocument.Parse(before);
        using var rightDocument = JsonDocument.Parse(after);
        var left = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var right = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Flatten(leftDocument.RootElement, "", left);
        Flatten(rightDocument.RootElement, "", right);
        return Array.AsReadOnly(left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(key => left.GetValueOrDefault(key) != right.GetValueOrDefault(key))
            .Select(key => new GraphScaffoldReviewChange(key, !left.ContainsKey(key) ? "Added" : !right.ContainsKey(key) ? "Removed" : "Changed",
                left.GetValueOrDefault(key), right.GetValueOrDefault(key))).ToArray());
    }

    private static void Flatten(JsonElement value, string path, SortedDictionary<string, string> result)
    {
        if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
        {
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                Flatten(property.Value, path + "/" + Escape(property.Name), result);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
        {
            // Named manifest/schema collections use stable semantic paths. Ordered keys and observations remain atomic arrays.
            var identity = path.Split('/').Last() switch
            {
                "nodes" => "label", "relationships" => "relationshipType",
                "properties" or "constraints" or "indexes" => "name", _ => null
            };
            var entries = value.EnumerateArray().ToArray();
            if (identity is not null && entries.All(e => e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty(identity, out var key) && key.ValueKind == JsonValueKind.String)
                && entries.Select(e => e.GetProperty(identity).GetString()).Distinct(StringComparer.Ordinal).Count() == entries.Length)
            {
                foreach (var entry in entries.OrderBy(e => e.GetProperty(identity).GetString(), StringComparer.Ordinal))
                    Flatten(entry, path + "/" + Escape(entry.GetProperty(identity).GetString()!), result);
                return;
            }
        }
        result[path] = JsonSerializer.Serialize(value);
    }

    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
