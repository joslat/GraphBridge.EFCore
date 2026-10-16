using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;

namespace GraphBridge.Tooling.Scaffolding;

/// <summary>Pure, deterministic inference from bounded observations and an explicitly reviewed authoring policy. Performs no I/O.</summary>
public static class GraphScaffoldCompiler
{
    public static GraphScaffoldCompilationResult Compile(GraphScaffoldSnapshot snapshot, GraphScaffoldPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        var diagnostics = new List<GraphGenerationDiagnostic>();
        void Warn(string code, string subject, string message) => diagnostics.Add(new(code, GraphGenerationSeverity.Warning, subject, message));
        void Error(string code, string subject, string message) => diagnostics.Add(new(code, GraphGenerationSeverity.Error, subject, message));
        var fingerprint = Fingerprint(snapshot, policy);
        Warn("BoundedObservation", "snapshot", "Samples are bounded and read-committed observations, not a consistent full graph snapshot. Types, requiredness, endpoint coverage and cardinality remain uncertain even below the sample limit.");
        Warn("ReviewRequired", "policy", "Generated configuration is a reviewable proposal. No graph writes, identity backfill, migration or schema deployment is performed by compilation.");
        if (policy.Namespace.Split('.').Any(string.IsNullOrWhiteSpace)) Error("InvalidNamespace", "policy", "A nonempty namespace with nonempty segments is required.");
        if (policy.Nodes.Select(n => n.ClrName).Concat(policy.Relationships.Select(r => r.ClrName)).Append(policy.ContextName)
            .Any(name => GraphCSharpNames.Clr(name) is "ScaffoldNodeDto" or "ScaffoldRelationshipDto"))
            Error("DtoNameCollision", "policy", "Tracked entity/context names must not collide with the generated read-only DTO names.");
        if (snapshot.Schema.GraphTypeSpecification is null)
            Warn("GraphTypeNotObserved", "schema", "Graph types were unavailable or not requested; constraints, indexes and bounded samples remain usable without Graph Types.");
        foreach (var group in policy.Nodes.GroupBy(n => n.Label, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Error("DuplicateMapping", group.Key, "Each node label can have only one reviewed mapping.");
        foreach (var group in policy.Relationships.GroupBy(n => n.RelationshipType, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Error("DuplicateMapping", group.Key, "Each relationship type can have only one reviewed mapping.");
        var labels = snapshot.Labels.Concat(snapshot.Nodes.SelectMany(n => n.Labels)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var types = snapshot.RelationshipTypes.Concat(snapshot.Relationships.Select(r => r.RelationshipType)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var label in labels)
            InspectPayload(label, snapshot.Nodes.Where(n => n.Labels.Contains(label, StringComparer.Ordinal)).Select(n => n.Properties));
        foreach (var type in types)
            InspectPayload(type, snapshot.Relationships.Where(r => r.RelationshipType == type).Select(r => r.Properties));
        foreach (var label in labels.Where(label => !policy.Nodes.Any(n => n.Label == label)))
            Warn("ReadOnlyNoReviewedKey", label, "No reviewed constraint-backed application identity. Preserve this label as read-only DTO data; names, titles, sampled uniqueness and elementId() are never chosen as keys.");
        foreach (var type in types)
        {
            Warn("UnknownCardinality", type, "Bounded relationship samples cannot establish one-to-one, one-to-many or association uniqueness. No collection navigation or association-edge identity is inferred.");
            var samples = snapshot.Relationships.Where(r => r.RelationshipType == type).ToArray();
            if (samples.Any(r => r.ObservedParallel)) Warn("ObservedParallelEdges", type, "Parallel relationships were observed; an independently identified edge is required for tracking.");
            if (!policy.Relationships.Any(r => r.RelationshipType == type))
                Warn("ReadOnlyRelationship", type, "Relationship payload and endpoints remain read-only DTO data until an explicit identified-edge mapping is reviewed.");
        }
        foreach (var node in snapshot.Nodes.Where(n => n.Labels.Count > 1))
        {
            var subject = string.Join("+", node.Labels);
            Warn("MultipleLabels", subject, "One observed node has multiple labels. No inheritance, extra-label preservation on insert or disjoint entity identity is inferred.");
            if (node.Labels.Count(label => policy.Nodes.Any(n => n.Label == label)) > 1)
                Error("OverlappingTrackedLabels", subject, "The same sampled node would belong to multiple tracked node mappings. Select one storage label or resolve the overlapping model explicitly.");
        }
        var nodes = new List<GraphNodeDefinition>();
        foreach (var mapping in policy.Nodes.OrderBy(n => n.Label, StringComparer.Ordinal))
        {
            if (!labels.Contains(mapping.Label, StringComparer.Ordinal)) Error("UnknownLabel", mapping.Label, "Reviewed label was not present in the inventory or samples.");
            var samples = snapshot.Nodes.Where(n => n.Labels.Contains(mapping.Label, StringComparer.Ordinal)).Select(n => n.Properties).ToArray();
            ValidateKey(snapshot.Schema, "NODE", mapping.Label, mapping.KeyProperties, mapping.ApproveRequiredKey, samples, diagnostics);
            var properties = Properties(mapping.Label, samples, mapping.Properties, mapping.KeyProperties, diagnostics);
            var keys = mapping.KeyProperties.Select(key => properties.FirstOrDefault(p => p.StoreName == key)?.ClrName ?? key).ToArray();
            nodes.Add(new(mapping.ClrName, null, mapping.Label, properties, keys, GraphDeletionPolicy.Restrict));
        }
        var edges = new List<GraphIdentifiedEdgeDefinition>();
        foreach (var mapping in policy.Relationships.OrderBy(r => r.RelationshipType, StringComparer.Ordinal))
        {
            var subject = mapping.RelationshipType;
            if (!types.Contains(subject, StringComparer.Ordinal)) Error("UnknownRelationshipType", subject, "Reviewed relationship type was not present in the inventory or samples.");
            var source = nodes.Where(n => n.Label == mapping.SourceLabel).ToArray();
            var target = nodes.Where(n => n.Label == mapping.TargetLabel).ToArray();
            if (source.Length != 1 || target.Length != 1)
            {
                Error("UnresolvedEndpoint", subject, "Both endpoint labels must resolve to one explicitly keyed node mapping.");
                continue;
            }
            var observed = snapshot.Relationships.Where(r => r.RelationshipType == subject).ToArray();
            if (observed.Any(r => !r.SourceLabels.Contains(mapping.SourceLabel, StringComparer.Ordinal)
                || !r.TargetLabels.Contains(mapping.TargetLabel, StringComparer.Ordinal)))
                Error("ConflictingEndpoints", subject, "Observed directed endpoints contradict the reviewed source/target labels. A single type mapping must not silently drop other observed endpoint shapes.");
            var samples = observed.Select(r => r.Properties).ToArray();
            ValidateKey(snapshot.Schema, "RELATIONSHIP", subject, [mapping.KeyProperty], mapping.ApproveRequiredKey, samples, diagnostics);
            var properties = Properties(subject, samples, mapping.Properties, [mapping.KeyProperty], diagnostics).ToList();
            var key = properties.FirstOrDefault(p => p.StoreName == mapping.KeyProperty)?.ClrName ?? mapping.KeyProperty;
            var sourceKeys = AddEndpoint(source[0], "Source", properties);
            var targetKeys = AddEndpoint(target[0], "Target", properties);
            Warn("IdentifiedEdgeDecision", subject, "Explicit identified-edge mapping selected, preserving payload and parallel identity; no association-edge or collection-navigation inference is performed.");
            edges.Add(new(mapping.ClrName, null, subject, properties, key,
                new(source[0].ClrName, "Source", sourceKeys, GraphDeletionPolicy.Restrict),
                new(target[0].ClrName, "Target", targetKeys, GraphDeletionPolicy.Restrict), GraphEdgeDirection.SourceToTarget));
        }
        GraphGenerationModel? model = nodes.Count == 0 ? null : new(policy.Namespace, policy.ContextName, nodes, edges);
        if (model is not null) diagnostics.AddRange(GraphCSharpEmitter.Generate(model).Diagnostics);
        else if (policy.Relationships.Count > 0) Error("MissingNodeMappings", "policy", "Tracked relationships require reviewed node mappings.");
        var ordered = diagnostics.Distinct().OrderBy(d => d.Subject, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal).ToArray();
        var files = new[] { new GraphGeneratedFile("ScaffoldReadOnlyDtos.g.cs", Dtos(policy.Namespace)),
            new GraphGeneratedFile("inference-report.md", Report(snapshot, policy, labels, types, ordered, fingerprint)) };
        return new(model, ordered, files, fingerprint);

        void InspectPayload(string owner, IEnumerable<IReadOnlyDictionary<string, GraphScaffoldValueKind>> observations)
        {
            var samples = observations.ToArray();
            foreach (var name in samples.SelectMany(s => s.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var kinds = samples.Where(s => s.ContainsKey(name)).Select(s => s[name]).Distinct().Order().ToArray();
                if (kinds.Length > 1) Warn("MixedPropertyTypes", owner + "." + name,
                    "Conflicting sampled storage types: " + string.Join(", ", kinds) + ". No type coercion is inferred.");
                if (kinds.Any(k => k is not (GraphScaffoldValueKind.String or GraphScaffoldValueKind.Boolean or GraphScaffoldValueKind.Integer or GraphScaffoldValueKind.Float)))
                    Warn("OpaquePayload", owner + "." + name, "Payload requires an explicit mapping decision. Raw DTO property bags preserve values; lists, temporal and spatial values are not supported by automatic tracked scalar inference.");
            }
        }
    }

    private static IReadOnlyList<string> AddEndpoint(GraphNodeDefinition node, string role, List<GraphPropertyDefinition> properties)
    {
        var keys = new List<string>();
        foreach (var name in node.KeyProperties)
        {
            var principal = node.Properties.FirstOrDefault(p => p.ClrName == name);
            if (principal is null) continue; // Existing key diagnostics prevent emitting this proposed model.
            var generatedName = role + name;
            keys.Add(generatedName);
            properties.Add(new(generatedName, null, generatedName, principal.ScalarType, true, principal.Conversion));
        }
        return keys;
    }

    private static void ValidateKey(GraphSchemaObservation schema, string entityType, string storage,
        IReadOnlyList<string> keys, bool approved, IReadOnlyList<IReadOnlyDictionary<string, GraphScaffoldValueKind>> samples,
        List<GraphGenerationDiagnostic> diagnostics)
    {
        void Error(string code, string message) => diagnostics.Add(new(code, GraphGenerationSeverity.Error, storage, message));
        if (!approved) Error("KeyReviewRequired", "Tracking requires explicit review of stable application identity and required key properties.");
        if (keys.Count == 0 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count || keys.Any(k => !GraphCSharpNames.IsStoreIdentifier(k)))
        {
            Error("InvalidApplicationKey", "Declare distinct application property names; elementId() and internal IDs are not durable application identity.");
            return;
        }
        var constraints = schema.Constraints.Where(c => c.EntityType == entityType && c.LabelsOrTypes is { Count: 1 }
            && c.LabelsOrTypes[0] == storage && c.Properties is not null).ToArray();
        bool KeyConstraint(GraphObservedConstraint c) => c.Type == (entityType == "NODE" ? "NODE_KEY" : "RELATIONSHIP_KEY");
        bool Unique(GraphObservedConstraint c) => KeyConstraint(c) || (entityType == "NODE"
            ? c.Type is "UNIQUENESS" or "NODE_PROPERTY_UNIQUENESS"
            : c.Type is "RELATIONSHIP_UNIQUENESS" or "RELATIONSHIP_PROPERTY_UNIQUENESS");
        bool SameKey(GraphObservedConstraint c) => c.Properties!.Count == keys.Count && c.Properties.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal));
        var candidates = constraints.Where(c => Unique(c) && SameKey(c)).ToArray();
        if (candidates.Length == 0) Error("MissingUniqueConstraint", "The complete reviewed key is not backed by an observed uniqueness/key constraint. An index or unique-looking sample is insufficient.");
        if (samples.Any(sample => keys.Any(key => !sample.ContainsKey(key))))
            Error("ObservedMissingKey", "A sampled entity lacks at least one proposed key property; review or perform a separate explicit data migration before tracking.");
        var existence = keys.All(key => candidates.Any(KeyConstraint) || constraints.Any(c =>
            c.Type == (entityType == "NODE" ? "NODE_PROPERTY_EXISTENCE" : "RELATIONSHIP_PROPERTY_EXISTENCE")
            && c.Properties!.Count == 1 && c.Properties[0] == key));
        if (!existence) diagnostics.Add(new("KeyExistenceUnproven", GraphGenerationSeverity.Warning, storage,
            "Uniqueness does not imply existence. Requiredness is a reviewed application assumption, not proven by bounded samples; unsampled records may fail materialization. Use a separate reviewed backfill/validation plan if needed."));
    }

    private static IReadOnlyList<GraphPropertyDefinition> Properties(string owner,
        IReadOnlyList<IReadOnlyDictionary<string, GraphScaffoldValueKind>> samples,
        IReadOnlyList<GraphScaffoldPropertyOverride> overrides, IReadOnlyList<string> keys,
        List<GraphGenerationDiagnostic> diagnostics)
    {
        var result = new List<GraphPropertyDefinition>();
        foreach (var duplicate in overrides.GroupBy(p => p.StoreName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            diagnostics.Add(new("DuplicatePropertyOverride", GraphGenerationSeverity.Error, owner + "." + duplicate.Key, "Each storage property can have only one reviewed decision."));
        var names = samples.SelectMany(s => s.Keys).Concat(overrides.Select(p => p.StoreName)).Concat(keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var subject = owner + "." + name;
            var authored = overrides.FirstOrDefault(p => p.StoreName == name);
            var kinds = samples.Where(s => s.ContainsKey(name)).Select(s => s[name]).Distinct().Order().ToArray();
            if (kinds.Length > 1) diagnostics.Add(new("MixedPropertyTypes", GraphGenerationSeverity.Warning, subject,
                "Conflicting sampled storage types: " + string.Join(", ", kinds) + ". No type coercion is inferred."));
            if (authored?.Ignore == true)
            {
                diagnostics.Add(new(keys.Contains(name, StringComparer.Ordinal) ? "IgnoredKey" : "ExplicitlyIgnoredPayload",
                    keys.Contains(name, StringComparer.Ordinal) ? GraphGenerationSeverity.Error : GraphGenerationSeverity.Warning,
                    subject, "Explicitly excluded from tracked EF mapping; raw values remain available in read-only DTO property bags. Existing omitted payload is not converted or rewritten."));
                continue;
            }
            var scalar = authored?.ScalarType ?? (kinds.Length == 1 ? kinds[0] switch
            {
                GraphScaffoldValueKind.String => GraphScalarType.String,
                GraphScaffoldValueKind.Boolean => GraphScalarType.Boolean,
                GraphScaffoldValueKind.Integer => GraphScalarType.Int64,
                GraphScaffoldValueKind.Float => GraphScalarType.Double,
                _ => (GraphScalarType?)null
            } : null);
            if (scalar is null)
            {
                diagnostics.Add(new("PropertyConversionRequired", GraphGenerationSeverity.Error, subject,
                    "No supported unambiguous scalar mapping exists. Review a supported stored-format conversion, or explicitly exclude this property and retain it in read-only DTOs; lists, temporal and spatial values are not silently stringified."));
                continue;
            }
            var expectedKind = scalar.Value switch
            {
                GraphScalarType.String or GraphScalarType.Guid or GraphScalarType.DateOnly => GraphScaffoldValueKind.String,
                GraphScalarType.Boolean => GraphScaffoldValueKind.Boolean,
                GraphScalarType.Int32 or GraphScalarType.Int64 => GraphScaffoldValueKind.Integer,
                GraphScalarType.Double => GraphScaffoldValueKind.Float,
                _ => GraphScaffoldValueKind.Unsupported
            };
            if (kinds.Any(kind => kind != expectedKind)) diagnostics.Add(new("IncompatibleStoredType", GraphGenerationSeverity.Error, subject,
                "The selected scalar mapping cannot read every observed storage type. A converter declaration does not transform existing graph values; use a separately reviewed migration or DTO-only mapping."));
            var required = keys.Contains(name, StringComparer.Ordinal) || authored?.Required == true;
            if (required && samples.Any(s => !s.ContainsKey(name))) diagnostics.Add(new("ObservedMissingRequiredProperty", GraphGenerationSeverity.Error, subject, "Required reviewed property is absent in at least one sample."));
            if (authored is not null && !authored.Ignore)
                diagnostics.Add(new("ReviewedPropertyAssumption", GraphGenerationSeverity.Warning, subject,
                    "Explicit type/conversion/requiredness is an application assumption; bounded type facts do not validate value range, Guid syntax, calendar format or all existing data."));
            result.Add(new(authored?.ClrName ?? name, null, name, scalar.Value, required, authored?.Conversion ?? GraphScalarConversion.Native));
        }
        return result;
    }

    private static string Dtos(string ns) => "// <auto-generated />\n#nullable enable\nnamespace " + GraphCSharpNames.Namespace(ns) + ";\n\n"
        + "// Untracked read-only browsing shapes. Property bags preserve unsupported payload such as roles arrays.\n"
        + "// Populate from the explicit native DTO lane; these types are not DbSet entities and have no durable elementId key.\n"
        + "public sealed record ScaffoldNodeDto(global::System.Collections.Generic.IReadOnlyList<string> Labels, global::System.Collections.Generic.IReadOnlyDictionary<string, object?> Properties);\n"
        + "public sealed record ScaffoldRelationshipDto(string RelationshipType, global::System.Collections.Generic.IReadOnlyList<string> SourceLabels, global::System.Collections.Generic.IReadOnlyList<string> TargetLabels, global::System.Collections.Generic.IReadOnlyDictionary<string, object?> Properties);\n";

    private static string Report(GraphScaffoldSnapshot snapshot, GraphScaffoldPolicy policy, string[] labels, string[] types,
        IReadOnlyList<GraphGenerationDiagnostic> diagnostics, string fingerprint)
    {
        var text = new StringBuilder("# Database-first inference proposal\n\n");
        text.Append("Source observation/policy SHA-256: `").Append(fingerprint).Append("` (not a runtime binding fingerprint).\n\n");
        text.Append("Server: ").Append(JsonSerializer.Serialize(snapshot.Capabilities.ServerVersion)).Append("; edition: ")
            .Append(JsonSerializer.Serialize(snapshot.Capabilities.Edition)).Append("; sample limit per selected label/type: ").Append(snapshot.SampleLimit).Append(".\n\n");
        text.Append("No writes were performed by this pure compiler. The finalized generated EF IModel must be compiled separately to obtain the actual binding manifest. No timestamp, connection address, credentials, raw property values or element IDs are included.\n\n");
        text.Append("## Schema evidence and reviewed decisions\n\n");
        foreach (var constraint in snapshot.Schema.Constraints)
            text.Append("- Constraint ").Append(JsonSerializer.Serialize(constraint.Name)).Append(": ")
                .Append(JsonSerializer.Serialize(constraint.EntityType)).Append(' ').Append(JsonSerializer.Serialize(constraint.Type))
                .Append(" on ").Append(JsonSerializer.Serialize(constraint.LabelsOrTypes)).Append(" properties ")
                .Append(JsonSerializer.Serialize(constraint.Properties)).Append(".\n");
        foreach (var index in snapshot.Schema.Indexes)
            text.Append("- Index ").Append(JsonSerializer.Serialize(index.Name)).Append(": ").Append(JsonSerializer.Serialize(index.Type))
                .Append(" state ").Append(JsonSerializer.Serialize(index.State)).Append(" on ")
                .Append(JsonSerializer.Serialize(index.LabelsOrTypes)).Append(" properties ").Append(JsonSerializer.Serialize(index.Properties))
                .Append("; indexes alone are not identity constraints.\n");
        foreach (var mapping in policy.Nodes.OrderBy(n => n.Label, StringComparer.Ordinal))
            text.Append("- Reviewed node ").Append(JsonSerializer.Serialize(mapping.Label)).Append(" -> ")
                .Append(JsonSerializer.Serialize(mapping.ClrName)).Append("; ordered application key ")
                .Append(JsonSerializer.Serialize(mapping.KeyProperties)).Append("; key/requiredness review acknowledged: ")
                .Append(mapping.ApproveRequiredKey ? "true" : "false").Append(".\n");
        foreach (var mapping in policy.Relationships.OrderBy(r => r.RelationshipType, StringComparer.Ordinal))
            text.Append("- Reviewed identified edge ").Append(JsonSerializer.Serialize(mapping.RelationshipType)).Append(" -> ")
                .Append(JsonSerializer.Serialize(mapping.ClrName)).Append("; application key ").Append(JsonSerializer.Serialize(mapping.KeyProperty))
                .Append("; source ").Append(JsonSerializer.Serialize(mapping.SourceLabel)).Append(" -> target ")
                .Append(JsonSerializer.Serialize(mapping.TargetLabel)).Append("; key/requiredness review acknowledged: ")
                .Append(mapping.ApproveRequiredKey ? "true" : "false").Append(".\n");
        text.Append('\n');
        text.Append("## Observed inventory and sample distributions\n\n");
        foreach (var label in labels)
        {
            var samples = snapshot.Nodes.Where(n => n.Labels.Contains(label, StringComparer.Ordinal)).ToArray();
            text.Append("- Node ").Append(JsonSerializer.Serialize(label)).Append(": ").Append(samples.Length).Append(" sampled observations; ")
                .Append(policy.Nodes.Any(n => n.Label == label) ? "reviewed tracking proposal" : "read-only DTO").Append(".\n");
            Distribution(samples.Select(n => n.Properties));
        }
        foreach (var type in types)
        {
            var samples = snapshot.Relationships.Where(r => r.RelationshipType == type).ToArray();
            text.Append("- Directed relationship ").Append(JsonSerializer.Serialize(type)).Append(": ").Append(samples.Length).Append(" sampled observations; cardinality unknown.\n");
            foreach (var endpoint in samples.Select(r => JsonSerializer.Serialize(r.SourceLabels) + " -> " + JsonSerializer.Serialize(r.TargetLabels)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                text.Append("  - Endpoint labels: ").Append(endpoint).Append(".\n");
            Distribution(samples.Select(r => r.Properties));
        }
        text.Append("\n## Decisions and uncertainty\n\n");
        foreach (var diagnostic in diagnostics)
            text.Append("- ").Append(diagnostic.Severity).Append(' ').Append(diagnostic.Code).Append(" [")
                .Append(JsonSerializer.Serialize(diagnostic.Subject)).Append("]: ").Append(diagnostic.Message).Append('\n');
        return text.ToString();
        void Distribution(IEnumerable<IReadOnlyDictionary<string, GraphScaffoldValueKind>> input)
        {
            var samples = input.ToArray();
            foreach (var name in samples.SelectMany(s => s.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                text.Append("  - ").Append(JsonSerializer.Serialize(name)).Append(": ")
                    .Append(string.Join(", ", samples.Where(s => s.ContainsKey(name)).GroupBy(s => s[name]).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count())))
                    .Append("; absent=").Append(samples.Count(s => !s.ContainsKey(name))).Append(".\n");
        }
    }

    private static string Fingerprint(GraphScaffoldSnapshot snapshot, GraphScaffoldPolicy policy)
    {
        static string[] Sorted<T>(IEnumerable<T> values) => values.Select(value => JsonSerializer.Serialize(value)).Order(StringComparer.Ordinal).ToArray();
        var canonical = JsonSerializer.Serialize(new
        {
            ContractVersion = 1,
            snapshot.Capabilities.ServerVersion, snapshot.Capabilities.Edition, snapshot.Capabilities.AdvertisedCypherVersions,
            Constraints = Sorted(snapshot.Schema.Constraints), Indexes = Sorted(snapshot.Schema.Indexes), snapshot.Schema.GraphTypeSpecification,
            snapshot.Labels, snapshot.RelationshipTypes, snapshot.SampleLimit,
            Nodes = Sorted(snapshot.Nodes), Relationships = Sorted(snapshot.Relationships),
            policy.Namespace, policy.ContextName,
            Mappings = Sorted(policy.Nodes.Select(n => new { n.Label, n.ClrName, n.KeyProperties, n.ApproveRequiredKey, Properties = Sorted(n.Properties) })),
            EdgeMappings = Sorted(policy.Relationships.Select(r => new { r.RelationshipType, r.ClrName, r.SourceLabel, r.TargetLabel, r.KeyProperty, r.ApproveRequiredKey, Properties = Sorted(r.Properties) }))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
