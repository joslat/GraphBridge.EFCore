using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace GraphBridge.Generators;

/// <summary>
/// Compile-time expectation for the deliberately closed mapping contract. Never supplies runtime metadata.
/// Byte compatibility with GraphSchemaManifest schema 1 is tested against finalized generated and manual EF models.
/// All persisted strings are validated ASCII identifiers or fixed descriptors; no general-purpose JSON input is accepted.
/// </summary>
internal static class ExpectedWitness
{
    internal static string Write(MappingContract model)
    {
        var caveats = new SortedSet<string>(StringComparer.Ordinal);
        var nodes = model.Entities.Where(e => !e.Edge).OrderBy(e => e.Token, StringComparer.Ordinal).Select(node =>
            "{\"label\":" + Quote(node.Token) + ",\"keyProperties\":" + Array(node.Keys.Select(k => node.Properties.Single(p => p.Symbol.Name == k).Name))
            + ",\"alternateKeys\":[],\"properties\":" + Properties(node, node.Properties, "node:" + node.Token)
            + ",\"indexes\":[],\"deleteBehavior\":\"restrict-existing-relationships\"}").ToArray();
        var relationships = model.Entities.Where(e => e.Edge).OrderBy(e => e.Token, StringComparer.Ordinal).Select(edge =>
        {
            var omitted = new HashSet<string>(edge.Source!.Keys.Concat(edge.Target!.Keys), StringComparer.Ordinal);
            // EF's conventional FK indexes contain only endpoint slots, so no edge index is stored.
            caveats.Add("relationship:" + edge.Token + ": indexes containing endpoint slots are excluded; endpoint FK values are not stored relationship properties.");
            return "{\"relationshipType\":" + Quote(edge.Token) + ",\"identity\":\"ApplicationId\",\"keyProperty\":"
                + Quote(edge.Properties.Single(p => p.Symbol.Name == edge.Keys[0]).Name)
                + ",\"direction\":\"source-to-target\",\"source\":" + Endpoint(edge.Source!) + ",\"target\":" + Endpoint(edge.Target!)
                + ",\"properties\":" + Properties(edge, edge.Properties.Where(p => !omitted.Contains(p.Symbol.Name)), "relationship:" + edge.Token)
                + ",\"indexes\":[],\"deleteBehavior\":\"relationship-only\"}";
        }).ToArray();
        return "{\"schemaVersion\":1,\"nodes\":[" + string.Join(",", nodes) + "],\"relationships\":[" + string.Join(",", relationships)
            + "],\"caveats\":" + Array(caveats) + "}";

        string Endpoint(EndpointContract endpoint)
        {
            var node = model.Entities.Single(e => SymbolEqualityComparer.Default.Equals(e.Symbol, endpoint.Node));
            return "{\"nodeLabel\":" + Quote(node.Token) + ",\"keyProperties\":"
                + Array(node.Keys.Select(k => node.Properties.Single(p => p.Symbol.Name == k).Name)) + ",\"efDeleteBehavior\":\"Restrict\"}";
        }
        string Properties(EntityContract owner, IEnumerable<PropertyContract> properties, string subject)
            => "[" + string.Join(",", properties.OrderBy(p => p.Name, StringComparer.Ordinal).Select(property =>
            {
                var isKey = owner.Keys.Contains(property.Symbol.Name, StringComparer.Ordinal);
                var version = property.Concurrency == 2;
                if (property.Conversion == 2)
                    caveats.Add(subject + "." + property.Name + ": opaque conversion; equal fingerprints describe storage shape, not converter semantic equivalence.");
                var wire = property.Scalar is "guid" or "date" or "string" ? "STRING"
                    : property.Scalar is "int" or "long" ? "INTEGER" : property.Scalar == "bool" ? "BOOLEAN" : "FLOAT";
                return "{\"name\":" + Quote(property.Name) + ",\"providerType\":" + Quote(wire)
                    + ",\"isRequired\":" + Bool(property.Required) + ",\"isKey\":" + Bool(isKey)
                    + ",\"concurrency\":" + Quote(property.Concurrency == 2 ? "IncrementingInt64" : property.Concurrency == 1 ? "OriginalValue" : "None")
                    + ",\"valueGeneration\":" + Quote(version ? "OnAddOrUpdate" : "Never")
                    + ",\"beforeSaveBehavior\":" + Quote(version ? "Ignore" : "Save")
                    + ",\"afterSaveBehavior\":" + Quote(version ? "Ignore" : isKey ? "Throw" : "Save")
                    + ",\"conversion\":" + Quote(property.Conversion == 1 ? "guid-d-string" : property.Conversion == 2 ? "opaque" : "native") + "}";
            })) + "]";
    }
    internal static string Hash(string value)
    {
        using var algorithm = SHA256.Create();
        return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }
    private static string Quote(string value) => "\"" + value + "\"";
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Array(IEnumerable<string> values) => "[" + string.Join(",", values.Select(Quote)) + "]";
}
