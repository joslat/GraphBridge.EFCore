using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Schema;

/// <summary>An immutable storage contract derived from a finalized GraphBridge EF model.</summary>
/// <remarks>
/// The fingerprint covers the versioned canonical storage description, not CLR names or arbitrary
/// conversion code. Review <see cref="Caveats"/> before treating equal fingerprints as model parity.
/// Creating a manifest does not access a database, deploy schema, or replace the EF model authority.
/// </remarks>
public sealed class GraphSchemaManifest
{
    internal GraphSchemaManifest(IEnumerable<GraphSchemaNode> nodes,
        IEnumerable<GraphSchemaRelationship> relationships, IEnumerable<string> caveats)
    {
        Nodes = Array.AsReadOnly(nodes.OrderBy(n => n.Label, StringComparer.Ordinal).ToArray());
        Relationships = Array.AsReadOnly(relationships.OrderBy(r => r.RelationshipType, StringComparer.Ordinal)
            .ThenBy(r => r.Source.NodeLabel, StringComparer.Ordinal).ThenBy(r => r.Target.NodeLabel, StringComparer.Ordinal).ToArray());
        Caveats = Array.AsReadOnly(caveats.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        CanonicalJson = WriteCanonicalJson();
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson))).ToLowerInvariant();
    }

    public int SchemaVersion => Relationships.Any(r => r.RequiredIncomingLinks == 0) ? 2 : 1;
    public IReadOnlyList<GraphSchemaNode> Nodes { get; }
    public IReadOnlyList<GraphSchemaRelationship> Relationships { get; }
    public IReadOnlyList<string> Caveats { get; }
    public string CanonicalJson { get; }
    /// <summary>Lowercase SHA-256 of the UTF-8 canonical JSON, without a byte-order mark.</summary>
    public string Fingerprint { get; }

    public static GraphSchemaManifest Create(IModel model) => GraphSchemaManifestCompiler.Create(model);

    private string WriteCanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", SchemaVersion);
            json.WriteStartArray("nodes");
            foreach (var node in Nodes)
            {
                json.WriteStartObject();
                json.WriteString("label", node.Label);
                // Written only when present, so the manifests and fingerprints of models without them don't change (ADR 0055).
                if (node.AdditionalLabels.Count > 0) Strings(json, "additionalLabels", node.AdditionalLabels);
                Strings(json, "keyProperties", node.KeyProperties);
                Indexes(json, node.AlternateKeys, "alternateKeys");
                Properties(json, node.Properties);
                Indexes(json, node.Indexes);
                json.WriteString("deleteBehavior", node.DeleteBehavior);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("relationships");
            foreach (var relationship in Relationships)
            {
                json.WriteStartObject();
                json.WriteString("relationshipType", relationship.RelationshipType);
                json.WriteString("identity", relationship.Identity.ToString());
                json.WriteString("keyProperty", relationship.KeyProperty);
                json.WriteString("direction", relationship.Direction);
                Endpoint(json, "source", relationship.Source);
                Endpoint(json, "target", relationship.Target);
                Properties(json, relationship.Properties);
                Indexes(json, relationship.Indexes);
                json.WriteString("deleteBehavior", relationship.DeleteBehavior);
                if (relationship.Identity == GraphSchemaRelationshipIdentity.DependentNode)
                {
                    json.WriteNumber("requiredIncomingLinks", relationship.RequiredIncomingLinks!.Value);
                    if (SchemaVersion >= 2) json.WriteNumber("maximumIncomingLinks", relationship.MaximumIncomingLinks!.Value);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            Strings(json, "caveats", Caveats);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Strings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }

    private static void Endpoint(Utf8JsonWriter json, string name, GraphSchemaEndpoint endpoint)
    {
        json.WriteStartObject(name);
        json.WriteString("nodeLabel", endpoint.NodeLabel);
        Strings(json, "keyProperties", endpoint.KeyProperties);
        json.WriteString("efDeleteBehavior", endpoint.EfDeleteBehavior);
        json.WriteEndObject();
    }

    private static void Properties(Utf8JsonWriter json, IEnumerable<GraphSchemaProperty> properties)
    {
        json.WriteStartArray("properties");
        foreach (var property in properties)
        {
            json.WriteStartObject();
            json.WriteString("name", property.Name);
            json.WriteString("providerType", property.ProviderType);
            json.WriteBoolean("isRequired", property.IsRequired);
            json.WriteBoolean("isKey", property.IsKey);
            json.WriteString("concurrency", property.Concurrency.ToString());
            json.WriteString("valueGeneration", property.ValueGeneration);
            json.WriteString("beforeSaveBehavior", property.BeforeSaveBehavior);
            json.WriteString("afterSaveBehavior", property.AfterSaveBehavior);
            json.WriteString("conversion", property.Conversion);
            // Written only for vector properties, so manifests without vectors keep their fingerprints.
            if (property.VectorDimensions is { } dimensions) json.WriteNumber("vectorDimensions", dimensions);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void Indexes(Utf8JsonWriter json, IEnumerable<GraphSchemaIndex> indexes, string name = "indexes")
    {
        json.WriteStartArray(name);
        foreach (var index in indexes)
        {
            json.WriteStartObject();
            Strings(json, "properties", index.Properties);
            json.WriteBoolean("isUnique", index.IsUnique);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}

public enum GraphSchemaRelationshipIdentity { EndpointPair, ApplicationId, DependentNode }
public enum GraphSchemaConcurrency { None, OriginalValue, IncrementingInt64 }

/// <summary>A stored node shape. Key property order follows the finalized EF primary key.</summary>
public sealed class GraphSchemaNode
{
    internal GraphSchemaNode(string label, IEnumerable<string> keyProperties,
        IEnumerable<GraphSchemaProperty> properties, IEnumerable<GraphSchemaIndex> indexes,
        IEnumerable<GraphSchemaIndex>? alternateKeys = null, IEnumerable<string>? additionalLabels = null)
    {
        Label = label;
        AdditionalLabels = Array.AsReadOnly((additionalLabels ?? []).Order(StringComparer.Ordinal).ToArray());
        KeyProperties = Array.AsReadOnly(keyProperties.ToArray());
        AlternateKeys = Array.AsReadOnly((alternateKeys ?? [])
            .OrderBy(k => string.Join("\u001f", k.Properties), StringComparer.Ordinal).ToArray());
        if (AlternateKeys.Any(k => !k.IsUnique))
            throw new ArgumentException("Every alternate key must require uniqueness.", nameof(alternateKeys));
        Properties = Array.AsReadOnly(properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray());
        Indexes = Array.AsReadOnly(indexes.ToArray());
    }
    public string Label { get; }
    /// <summary>Further labels every node of the type carries (ADR 0055); empty without any.</summary>
    public IReadOnlyList<string> AdditionalLabels { get; }
    public IReadOnlyList<string> KeyProperties { get; }
    /// <summary>Required alternate uniqueness contracts, separate from the primary key and optional indexes.</summary>
    public IReadOnlyList<GraphSchemaIndex> AlternateKeys { get; }
    public IReadOnlyList<GraphSchemaProperty> Properties { get; }
    public IReadOnlyList<GraphSchemaIndex> Indexes { get; }
    public string DeleteBehavior => "restrict-existing-relationships";
}

/// <summary>A native relationship shape; endpoint FK slots are not stored relationship properties.</summary>
public sealed class GraphSchemaRelationship
{
    internal GraphSchemaRelationship(string relationshipType, GraphSchemaRelationshipIdentity identity,
        string? keyProperty, GraphSchemaEndpoint source, GraphSchemaEndpoint target,
        IEnumerable<GraphSchemaProperty> properties, IEnumerable<GraphSchemaIndex> indexes,
        IEnumerable<GraphSchemaDerivedForeignKey>? derivedForeignKeys = null, bool requiredReference = true)
    {
        if (!Enum.IsDefined(identity)) throw new NotSupportedException("Unknown GraphBridge relationship identity.");
        RelationshipType = relationshipType;
        Identity = identity;
        KeyProperty = keyProperty;
        Source = source;
        Target = target;
        Properties = Array.AsReadOnly(properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray());
        Indexes = Array.AsReadOnly(indexes.ToArray());
        DerivedForeignKeys = Array.AsReadOnly((derivedForeignKeys ?? []).ToArray());
        RequiredIncomingLinks = identity == GraphSchemaRelationshipIdentity.DependentNode ? requiredReference ? 1 : 0 : null;
        if (identity == GraphSchemaRelationshipIdentity.DependentNode)
        {
            if (keyProperty is not null || Properties.Count != 0 || Indexes.Count != 0 || DerivedForeignKeys.Count == 0
                || !DerivedForeignKeys.Select(slot => slot.PrincipalKeyProperty).SequenceEqual(source.KeyProperties, StringComparer.Ordinal)
                || DerivedForeignKeys.Select(slot => slot.DependentSlot).Distinct(StringComparer.Ordinal).Count() != DerivedForeignKeys.Count)
                throw new ArgumentException("Dependent-node relationships require ordered derived FK slots and no stored relationship properties, key or indexes.");
        }
        else if (DerivedForeignKeys.Count != 0 || !requiredReference)
            throw new ArgumentException("Derived dependent-node FK slots require dependent-node identity.");
    }
    public string RelationshipType { get; }
    public GraphSchemaRelationshipIdentity Identity { get; }
    public string? KeyProperty { get; }
    public string Direction => "source-to-target";
    public GraphSchemaEndpoint Source { get; }
    public GraphSchemaEndpoint Target { get; }
    public IReadOnlyList<GraphSchemaProperty> Properties { get; }
    public IReadOnlyList<GraphSchemaIndex> Indexes { get; }
    /// <summary>
    /// Ordered dependent logical slots projected from the source node key; these names are not stored properties.
    /// Review metadata only, excluded from canonical JSON/fingerprint like tracked-edge endpoint FK slots.
    /// Source.KeyProperties retains the ordered physical principal key in the canonical storage contract.
    /// </summary>
    public IReadOnlyList<GraphSchemaDerivedForeignKey> DerivedForeignKeys { get; }
    /// <summary>Minimum application-enforced incoming-link count: one for required, zero for optional; null for tracked edge entities.</summary>
    public int? RequiredIncomingLinks { get; }
    /// <summary>Maximum application-enforced incoming-link count; schema-v2 optional manifests serialize this explicitly.</summary>
    public int? MaximumIncomingLinks => Identity == GraphSchemaRelationshipIdentity.DependentNode ? 1 : null;
    public string DeleteBehavior => Identity == GraphSchemaRelationshipIdentity.DependentNode
        ? "remove-with-dependent-restrict-principal" : "relationship-only";
}

/// <summary>An ordered FK derivation. DependentSlot is the EF logical property name, never a property on the dependent node or relationship.</summary>
public sealed class GraphSchemaDerivedForeignKey
{
    internal GraphSchemaDerivedForeignKey(string dependentSlot, string principalKeyProperty)
        => (DependentSlot, PrincipalKeyProperty) = (dependentSlot, principalKeyProperty);
    public string DependentSlot { get; }
    public string PrincipalKeyProperty { get; }
}

public sealed class GraphSchemaEndpoint
{
    internal GraphSchemaEndpoint(string nodeLabel, IEnumerable<string> keyProperties, string efDeleteBehavior)
    {
        NodeLabel = nodeLabel;
        KeyProperties = Array.AsReadOnly(keyProperties.ToArray());
        EfDeleteBehavior = efDeleteBehavior;
    }
    public string NodeLabel { get; }
    public IReadOnlyList<string> KeyProperties { get; }
    /// <summary>
    /// EF client-side relationship behavior; this does not request a database cascade.
    /// For DependentNode both endpoints repeat the same governing node FK's behavior, not two independent foreign keys.
    /// </summary>
    public string EfDeleteBehavior { get; }
}

public sealed class GraphSchemaProperty
{
    internal GraphSchemaProperty(string name, string providerType, bool isRequired, bool isKey,
        GraphSchemaConcurrency concurrency, string valueGeneration, string beforeSaveBehavior,
        string afterSaveBehavior, string conversion, int? vectorDimensions = null)
        => (Name, ProviderType, IsRequired, IsKey, Concurrency, ValueGeneration, BeforeSaveBehavior, AfterSaveBehavior, Conversion, VectorDimensions)
            = (name, providerType, isRequired, isKey, concurrency, valueGeneration, beforeSaveBehavior, afterSaveBehavior, conversion, vectorDimensions);
    public string Name { get; }
    /// <summary>Neo4j wire type: STRING, INTEGER, FLOAT, BOOLEAN or ZONED DATETIME, or LIST&lt;FLOAT&gt; for a vector property.
    /// Both Int32 and Int64 use INTEGER.</summary>
    public string ProviderType { get; }
    /// <summary>The dimensions of a property mapped with <c>IsGraphVector</c>; null for scalar properties.</summary>
    public int? VectorDimensions { get; }
    public bool IsRequired { get; }
    /// <summary>Whether the stored property participates in any primary or alternate EF key.</summary>
    public bool IsKey { get; }
    public GraphSchemaConcurrency Concurrency { get; }
    public string ValueGeneration { get; }
    public string BeforeSaveBehavior { get; }
    public string AfterSaveBehavior { get; }
    /// <summary>Known format descriptor, or opaque when custom conversion semantics are not fingerprinted.</summary>
    public string Conversion { get; }
}

/// <summary>Index intent on stored properties; names and unavailable provider-specific index facets are excluded.</summary>
public sealed class GraphSchemaIndex
{
    internal GraphSchemaIndex(IEnumerable<string> properties, bool isUnique)
    {
        Properties = Array.AsReadOnly(properties.ToArray());
        IsUnique = isUnique;
    }
    public IReadOnlyList<string> Properties { get; }
    public bool IsUnique { get; }
}
