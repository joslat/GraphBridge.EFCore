using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.GraphRag;

/// <summary>
/// The extraction schema read from a finalized EF model (ADR 0052, section 7; GR-32): the ontology's node classes with their
/// mapped labels, the allowed (source, type, target) patterns and the quote types. <see cref="ToJsonSchema"/> constrains a
/// model's reply, <see cref="ToPromptText"/> describes the same for the instructions, and <see cref="Fingerprint"/> changes
/// whenever either would.
/// </summary>
public sealed class GraphRagModelSchema
{
    private GraphRagModelSchema(GraphRagOntology ontology, IReadOnlyList<GraphRagSchemaClass> classes, IReadOnlyList<GraphRagSchemaPattern> patterns,
        IReadOnlyList<GraphRagSchemaQuote> quotes)
    {
        (Ontology, Classes, Patterns, Quotes) = (ontology, classes, patterns, quotes);
        Fingerprint = GraphRagEmbeddingSet.Hash(ontology.Fingerprint + "\n" + ToJsonSchema() + "\n" + ToPromptText());
    }

    public GraphRagOntology Ontology { get; }
    public IReadOnlyList<GraphRagSchemaClass> Classes { get; }
    public IReadOnlyList<GraphRagSchemaPattern> Patterns { get; }
    public IReadOnlyList<GraphRagSchemaQuote> Quotes { get; }
    public string Fingerprint { get; }

    /// <summary>Reads the ontology <c>AddGraphRag</c> mapped, and checks every class, fact and quote type against the model's own labels and types.</summary>
    public static GraphRagModelSchema From(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var ontology = model.FindAnnotation(GraphRagModelBuilderExtensions.OntologyAnnotation)?.Value as GraphRagOntology
            ?? throw new InvalidOperationException("The model maps no GraphRAG ontology; pass one with modelBuilder.AddGraphRag(dimensions, o => o.Ontology = ...).");
        string Label(Type type)
            => model.FindEntityType(type)?[GraphAnnotationNames.NodeLabel] as string
                ?? throw new InvalidOperationException($"The ontology class '{type.Name}' isn't mapped as a node.");
        string Type(Type clrType, string expected)
        {
            var mapped = model.FindEntityType(clrType)?[GraphAnnotationNames.RelationshipType] as string;
            return mapped == expected ? mapped : throw new InvalidOperationException($"'{clrType.Name}' isn't mapped as the relationship type {expected}.");
        }
        var classes = ontology.Classes.Select(c => new GraphRagSchemaClass(c.Kind, Label(c.ClrType), c.Description, c.Aliases, c.Properties,
            Type(c.MentionClrType, c.MentionType))).ToArray();
        var patterns = ontology.Relationships.Select(r => new GraphRagSchemaPattern(r.FromKind, Type(r.ClrType, r.Type), r.ToKind, r.Description, r.Properties)).ToArray();
        var quotes = ontology.Quotes.Select(q => new GraphRagSchemaQuote(Type(q.ClrType, q.Type), q.SpeakerKind, q.Description, q.Addressee)).ToArray();
        return new(ontology, Array.AsReadOnly(classes), Array.AsReadOnly(patterns), Array.AsReadOnly(quotes));
    }

    /// <summary>
    /// The JSON Schema of an extraction reply: <c>entities</c>, <c>relationships</c> and, when the ontology has quote types,
    /// <c>quotes</c>. It meets strict structured output's rules: every object is closed and lists all its fields as required,
    /// and a field a reply may leave out is nullable instead.
    /// </summary>
    public string ToJsonSchema()
    {
        var kinds = new JsonArray(Classes.Select(c => (JsonNode)c.Kind).ToArray());
        var entity = Properties(("kind", new JsonObject { ["type"] = "string", ["enum"] = kinds }), ("name", Text()),
            ("aliases", Strings()), ("description", Optional()), ("mentions", Strings()));
        foreach (var property in Classes.SelectMany(c => c.Properties).Distinct(StringComparer.Ordinal)) entity[Camel(property)] = Optional();
        // The document is an endpoint kind only when a pattern links to it.
        var endpoints = Classes.Select(c => c.Kind).ToList();
        if (Patterns.Any(p => p.FromKind == GraphRagOntologyBuilder.DocumentKind || p.ToKind == GraphRagOntologyBuilder.DocumentKind))
            endpoints.Add(GraphRagOntologyBuilder.DocumentKind);
        JsonObject EndpointKind() => new() { ["type"] = "string", ["enum"] = new JsonArray(endpoints.Select(k => (JsonNode)k).ToArray()) };
        var relationship = Properties(("type", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Patterns.Select(p => (JsonNode)p.Type).Distinct().ToArray()) }),
            ("from", Text()), ("fromKind", EndpointKind()), ("to", Text()), ("toKind", EndpointKind()));
        foreach (var property in Patterns.SelectMany(p => p.Properties).Distinct(StringComparer.Ordinal)) relationship[Camel(property)] = Optional();
        var root = new JsonObject { ["entities"] = Array(entity), ["relationships"] = Array(relationship) };
        if (Quotes.Count > 0)
        {
            var quote = Properties(("type", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Quotes.Select(q => (JsonNode)q.Type).ToArray()) }),
                ("speaker", Text()), ("words", Text()));
            if (Quotes.Any(q => q.Addressee)) quote["addressee"] = Optional();
            root["quotes"] = Array(quote);
        }
        var schema = new JsonObject
        {
            ["type"] = "object", ["properties"] = root, ["required"] = new JsonArray(root.Select(p => (JsonNode)p.Key).ToArray()),
            ["additionalProperties"] = false
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        static JsonObject Text() => new() { ["type"] = "string" };
        static JsonObject Optional() => new() { ["type"] = new JsonArray("string", "null") };
        static JsonObject Strings() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        static JsonObject Properties(params (string Name, JsonNode Value)[] properties)
        {
            var result = new JsonObject();
            foreach (var (name, value) in properties) result[name] = value;
            return result;
        }
        static JsonObject Array(JsonObject properties) => new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray()),
                ["additionalProperties"] = false
            }
        };
    }

    /// <summary>The kinds, relationship patterns and quote types as plain text for the extraction instructions.</summary>
    public string ToPromptText()
    {
        var text = new StringBuilder("Kinds of things:\n");
        foreach (var c in Classes)
        {
            text.Append("- ").Append(c.Kind).Append(": ").Append(c.Description);
            var fields = new List<string> { "name" };
            if (c.Aliases) fields.Add("aliases");
            fields.Add("description");
            fields.AddRange(c.Properties.Select(Camel));
            text.Append(" Fields: ").AppendJoin(", ", fields).Append(".\n");
        }
        if (Patterns.Count > 0)
        {
            text.Append("\nRelationship types (type: from kind -> to kind):\n");
            foreach (var p in Patterns)
            {
                text.Append("- ").Append(p.Type).Append(": ").Append(p.FromKind).Append(" -> ").Append(p.ToKind).Append(". ").Append(p.Description);
                if (p.Properties.Count > 0) text.Append(" Fields: ").AppendJoin(", ", p.Properties.Select(Camel)).Append('.');
                text.Append('\n');
            }
        }
        if (Quotes.Count > 0)
        {
            text.Append("\nQuotations:\n");
            foreach (var q in Quotes) text.Append("- ").Append(q.Type).Append(": spoken by a ").Append(q.SpeakerKind).Append(". ").Append(q.Description).Append('\n');
        }
        return text.ToString();
    }

    internal static string Camel(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>A node class as mapped: the kind the model sees, its stored label, description, alias support, extra properties and mention type.</summary>
public sealed record GraphRagSchemaClass(string Kind, string Label, string Description, bool Aliases, IReadOnlyList<string> Properties, string MentionType);

/// <summary>An allowed fact pattern between kinds; <c>Document</c> stands for the source document.</summary>
public sealed record GraphRagSchemaPattern(string FromKind, string Type, string ToKind, string Description, IReadOnlyList<string> Properties);

/// <summary>A quote type and the kind of its speakers.</summary>
public sealed record GraphRagSchemaQuote(string Type, string SpeakerKind, string Description, bool Addressee);
