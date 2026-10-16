using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;

namespace GraphBridge.Tooling.Scaffolding;

/// <summary>Strict versioned offline interchange. Snapshots contain storage metadata and kinds, never property values or element IDs.</summary>
public static class GraphScaffoldJson
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string WriteSnapshot(GraphScaffoldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SnapshotBounds(snapshot);
        var result = Json(new
        {
            formatVersion = 1,
            capabilities = new { snapshot.Capabilities.ServerVersion, snapshot.Capabilities.Edition, snapshot.Capabilities.AdvertisedCypherVersions },
            schema = new { snapshot.Schema.Constraints, snapshot.Schema.Indexes, snapshot.Schema.GraphTypeSpecification },
            snapshot.Labels,
            snapshot.RelationshipTypes,
            nodes = snapshot.Nodes.Select(n => new { n.Labels, properties = Kinds(n.Properties) })
                .OrderBy(n => Json(n), StringComparer.Ordinal).ToArray(),
            relationships = snapshot.Relationships.Select(r => new { r.RelationshipType, r.SourceLabels, r.TargetLabels,
                properties = Kinds(r.Properties), r.ObservedParallel }).OrderBy(r => Json(r), StringComparer.Ordinal).ToArray(),
            snapshot.SampleLimit
        });
        _ = ReadSnapshot(result); // Writers obey the same bounds as readers.
        return result;
    }

    public static GraphScaffoldSnapshot ReadSnapshot(string json)
    {
        using var document = Parse(json);
        var root = Object(document.RootElement, "snapshot", "formatVersion", "capabilities", "schema", "labels", "relationshipTypes", "nodes", "relationships", "sampleLimit");
        Version(root);
        var capabilities = Object(Get(root, "capabilities"), "capabilities", "serverVersion", "edition", "advertisedCypherVersions");
        var schema = Object(Get(root, "schema"), "schema", "constraints", "indexes", "graphTypeSpecification");
        var constraints = Array(Get(schema, "constraints"), 10000).Select(item =>
        {
            var value = Object(item, "constraint", "name", "type", "entityType", "labelsOrTypes", "properties", "propertyType");
            return new GraphObservedConstraint(Text(value, "name"), Text(value, "type"), Text(value, "entityType"),
                NullableStrings(Get(value, "labelsOrTypes")), NullableStrings(Get(value, "properties")), OptionalText(value, "propertyType"));
        }).ToArray();
        var indexes = Array(Get(schema, "indexes"), 10000).Select(item =>
        {
            var value = Object(item, "index", "name", "state", "type", "entityType", "labelsOrTypes", "properties", "owningConstraint");
            return new GraphObservedIndex(Text(value, "name"), Text(value, "state"), Text(value, "type"), Text(value, "entityType"),
                NullableStrings(Get(value, "labelsOrTypes")), NullableStrings(Get(value, "properties")), OptionalText(value, "owningConstraint"));
        }).ToArray();
        var sampleLimit = Integer(Get(root, "sampleLimit"), 1, 10000);
        var nodes = Array(Get(root, "nodes"), 10000).Select(item =>
        {
            var value = Object(item, "node sample", "labels", "properties");
            return new GraphScaffoldNodeSample(Strings(Get(value, "labels")), Properties(Get(value, "properties")));
        }).ToArray();
        var relationships = Array(Get(root, "relationships"), 10000).Select(item =>
        {
            var value = Object(item, "relationship sample", "relationshipType", "sourceLabels", "targetLabels", "properties", "observedParallel");
            return new GraphScaffoldRelationshipSample(Text(value, "relationshipType"), Strings(Get(value, "sourceLabels")),
                Strings(Get(value, "targetLabels")), Properties(Get(value, "properties")), Boolean(Get(value, "observedParallel")));
        }).ToArray();
        var snapshot = new GraphScaffoldSnapshot(GraphSchemaCapabilities.Detect(Text(capabilities, "serverVersion"), Text(capabilities, "edition"),
                Strings(Get(capabilities, "advertisedCypherVersions"))),
            new(constraints, indexes, OptionalText(schema, "graphTypeSpecification", 1024 * 1024)),
            Strings(Get(root, "labels"), 10000), Strings(Get(root, "relationshipTypes"), 10000), nodes, relationships, sampleLimit);
        SnapshotBounds(snapshot);
        return snapshot;
    }

    public static string WritePolicy(GraphScaffoldPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PolicyBounds(policy);
        var result = Json(new
        {
            formatVersion = 1,
            policy.Namespace,
            policy.ContextName,
            nodes = policy.Nodes.OrderBy(n => n.Label, StringComparer.Ordinal).Select(n => new
            { n.Label, n.ClrName, n.KeyProperties, n.ApproveRequiredKey, properties = Overrides(n.Properties) }).ToArray(),
            relationships = policy.Relationships.OrderBy(r => r.RelationshipType, StringComparer.Ordinal).Select(r => new
            { r.RelationshipType, r.ClrName, r.SourceLabel, r.TargetLabel, r.KeyProperty, r.ApproveRequiredKey,
                properties = Overrides(r.Properties) }).ToArray()
        });
        _ = ReadPolicy(result);
        return result;
    }

    public static GraphScaffoldPolicy ReadPolicy(string json)
    {
        using var document = Parse(json);
        var root = Object(document.RootElement, "policy", "formatVersion", "namespace", "contextName", "nodes", "relationships");
        Version(root);
        var nodes = Array(Get(root, "nodes"), 1000).Select(item =>
        {
            var value = Object(item, "node mapping", "label", "clrName", "keyProperties", "approveRequiredKey", "properties");
            return new GraphScaffoldNodeMapping(Text(value, "label"), Text(value, "clrName"), Strings(Get(value, "keyProperties")),
                Boolean(Get(value, "approveRequiredKey")), ReadOverrides(Get(value, "properties")));
        }).ToArray();
        var relationships = Array(Get(root, "relationships"), 1000).Select(item =>
        {
            var value = Object(item, "relationship mapping", "relationshipType", "clrName", "sourceLabel", "targetLabel", "keyProperty", "approveRequiredKey", "properties");
            return new GraphScaffoldRelationshipMapping(Text(value, "relationshipType"), Text(value, "clrName"),
                Text(value, "sourceLabel"), Text(value, "targetLabel"), Text(value, "keyProperty"),
                Boolean(Get(value, "approveRequiredKey")), ReadOverrides(Get(value, "properties")));
        }).ToArray();
        var policy = new GraphScaffoldPolicy(Text(root, "namespace"), Text(root, "contextName"), nodes, relationships);
        PolicyBounds(policy);
        return policy;
    }

    private static GraphScaffoldPropertyOverride[] ReadOverrides(JsonElement element) => Array(element, 1000).Select(item =>
    {
        var value = Object(item, "property override", "storeName", "clrName", "scalarType", "conversion", "required", "ignore");
        return new GraphScaffoldPropertyOverride(Text(value, "storeName"), Text(value, "clrName"),
            EnumValue<GraphScalarType>(Get(value, "scalarType")), EnumValue<GraphScalarConversion>(Get(value, "conversion")),
            Boolean(Get(value, "required")), Boolean(Get(value, "ignore")));
    }).ToArray();

    private static object[] Overrides(IEnumerable<GraphScaffoldPropertyOverride> values) => values.OrderBy(v => v.StoreName, StringComparer.Ordinal)
        .Select(v => (object)new { v.StoreName, v.ClrName, scalarType = v.ScalarType.ToString(), conversion = v.Conversion.ToString(), v.Required, v.Ignore }).ToArray();
    private static SortedDictionary<string, string> Kinds(IReadOnlyDictionary<string, GraphScaffoldValueKind> values)
        => new(values.ToDictionary(v => v.Key, v => v.Value.ToString()), StringComparer.Ordinal);
    private static string Json(object value) => JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    // Bound public in-memory input before serialization/sorting creates additional copies. Readers apply the same budget.
    private static void SnapshotBounds(GraphScaffoldSnapshot snapshot)
    {
        if ((long)snapshot.Nodes.Count + snapshot.Relationships.Count > 10000
            || snapshot.Schema.Constraints.Count > 10000 || snapshot.Schema.Indexes.Count > 10000)
            throw Invalid("Snapshot exceeds its aggregate sample or schema row limit.");
        var budget = new TextBudget();
        budget.Text(snapshot.Capabilities.ServerVersion);
        budget.Text(snapshot.Capabilities.Edition);
        budget.Strings(snapshot.Capabilities.AdvertisedCypherVersions);
        budget.Strings(snapshot.Labels, 10000);
        budget.Strings(snapshot.RelationshipTypes, 10000);
        budget.Text(snapshot.Schema.GraphTypeSpecification, 1024 * 1024, allowControls: true);
        foreach (var constraint in snapshot.Schema.Constraints)
        {
            budget.Text(constraint.Name); budget.Text(constraint.Type); budget.Text(constraint.EntityType);
            budget.Strings(constraint.LabelsOrTypes); budget.Strings(constraint.Properties); budget.Text(constraint.PropertyType);
        }
        foreach (var index in snapshot.Schema.Indexes)
        {
            budget.Text(index.Name); budget.Text(index.State); budget.Text(index.Type); budget.Text(index.EntityType);
            budget.Strings(index.LabelsOrTypes); budget.Strings(index.Properties); budget.Text(index.OwningConstraint);
        }
        foreach (var node in snapshot.Nodes)
        {
            budget.Strings(node.Labels);
            if (node.Properties.Count > 1000) throw Invalid("Too many node properties.");
            foreach (var property in node.Properties) budget.Text(property.Key);
        }
        foreach (var relationship in snapshot.Relationships)
        {
            budget.Text(relationship.RelationshipType); budget.Strings(relationship.SourceLabels); budget.Strings(relationship.TargetLabels);
            if (relationship.Properties.Count > 1000) throw Invalid("Too many relationship properties.");
            foreach (var property in relationship.Properties) budget.Text(property.Key);
        }
    }

    private static void PolicyBounds(GraphScaffoldPolicy policy)
    {
        if (policy.Nodes.Count > 1000 || policy.Relationships.Count > 1000) throw Invalid("Too many policy mappings.");
        var budget = new TextBudget();
        budget.Text(policy.Namespace); budget.Text(policy.ContextName);
        foreach (var node in policy.Nodes)
        {
            budget.Text(node.Label); budget.Text(node.ClrName); budget.Strings(node.KeyProperties);
            Properties(node.Properties);
        }
        foreach (var relationship in policy.Relationships)
        {
            budget.Text(relationship.RelationshipType); budget.Text(relationship.ClrName);
            budget.Text(relationship.SourceLabel); budget.Text(relationship.TargetLabel); budget.Text(relationship.KeyProperty);
            Properties(relationship.Properties);
        }
        void Properties(IReadOnlyList<GraphScaffoldPropertyOverride> properties)
        {
            if (properties.Count > 1000) throw Invalid("Too many property overrides.");
            foreach (var property in properties)
            {
                budget.Text(property.StoreName); budget.Text(property.ClrName);
                if (!Enum.IsDefined(property.ScalarType) || !Enum.IsDefined(property.Conversion)) throw Invalid("Unknown policy enumeration value.");
            }
        }
    }

    private sealed class TextBudget
    {
        private int _fields;
        private long _bytes;
        internal void Text(string? value, int maximum = 1024, bool allowControls = false)
        {
            if (value is null) return;
            if (++_fields > 65536 || value.Length > maximum || !allowControls && (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
                throw Invalid("String or aggregate field limit exceeded.");
            _bytes += Encoding.UTF8.GetByteCount(value);
            if (_bytes > 1024 * 1024) throw Invalid("Aggregate text exceeds the 1 MiB limit.");
        }
        internal void Strings(IReadOnlyCollection<string>? values, int maximum = 1000)
        {
            if (values is null) return;
            if (values.Count > maximum) throw Invalid("String array limit exceeded.");
            foreach (var value in values) Text(value);
        }
    }

    private static JsonDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid("JSON exceeds the 4 MiB limit.");
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { throw Invalid("Malformed JSON or excessive nesting."); }
    }
    private static Dictionary<string, JsonElement> Object(JsonElement element, string subject, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(subject + " must be an object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value))
                throw Invalid(subject + " contains an unknown or duplicate field.");
        return result;
    }
    private static JsonElement Get(Dictionary<string, JsonElement> value, string name)
        => value.TryGetValue(name, out var element) ? element : throw Invalid("Missing required field: " + name);
    private static void Version(Dictionary<string, JsonElement> value) => _ = Integer(Get(value, "formatVersion"), 1, 1);
    private static string Text(Dictionary<string, JsonElement> value, string name) => String(Get(value, name));
    private static string String(JsonElement value, int maximum = 1024)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("Expected a string.");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw Invalid("String is empty, excessive, or contains control characters.");
        return text;
    }
    private static string? OptionalText(Dictionary<string, JsonElement> value, string name, int maximum = 1024)
    {
        if (!value.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        // Graph type specifications may legitimately contain line breaks.
        if (name == "graphTypeSpecification")
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString()!.Length > maximum) throw Invalid("Invalid graph type specification.");
            return element.GetString();
        }
        return String(element, maximum);
    }
    private static JsonElement[] Array(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Invalid("Expected a bounded array.");
        return value.EnumerateArray().ToArray();
    }
    private static string[] Strings(JsonElement value, int maximum = 1000)
    {
        var strings = Array(value, maximum).Select(v => String(v)).ToArray();
        if (strings.Distinct(StringComparer.Ordinal).Count() != strings.Length) throw Invalid("Duplicate string array item.");
        return strings;
    }
    private static string[]? NullableStrings(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Strings(value);
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Invalid("Expected a boolean.");
    private static int Integer(JsonElement value, int minimum, int maximum)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum
            ? number : throw Invalid("Integer is absent or outside its allowed range.");
    private static T EnumValue<T>(JsonElement value) where T : struct, Enum
    {
        var text = String(value);
        return Enum.TryParse<T>(text, ignoreCase: false, out var result) && Enum.IsDefined(result) && result.ToString() == text
            ? result : throw Invalid("Unknown enumeration value.");
    }
    private static IReadOnlyDictionary<string, GraphScaffoldValueKind> Properties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Properties must be an object.");
        var result = new SortedDictionary<string, GraphScaffoldValueKind>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 1024 || property.Name.Any(char.IsControl)
                || result.Count >= 1000 || !result.TryAdd(property.Name, EnumValue<GraphScaffoldValueKind>(property.Value)))
                throw Invalid("Invalid or duplicate property name, or too many properties.");
        }
        return result;
    }
    private static ArgumentException Invalid(string message) => new("GraphBridge scaffold JSON: " + message);
}
