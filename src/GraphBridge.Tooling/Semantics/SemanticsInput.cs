using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Parsing.Handlers;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GraphBridge.Tooling.Semantics;

internal static class SemanticsInput
{
    internal const int MaxBytes = 1_048_576;
    internal const int MaxTriples = 20_000;
    internal const int MaxDepth = 32;

    internal static Graph Turtle(string text)
    {
        Bound(text);
        CheckTurtleDepth(text);
        var graph = new Graph();
        try
        {
            // Explicit W3C Turtle, without RDF-star, imports, URI loading, configuration or inference.
            new TurtleParser(TurtleSyntax.W3C, true).Load(new BoundedGraphHandler(graph), new StringReader(text));
            return graph;
        }
        catch { graph.Dispose(); throw; }
    }

    internal static PersistencePolicy Policy(string text)
    {
        Bound(text);
        var parser = new Parser(new StringReader(text));
        var depth = 0;
        var documents = 0;
        while (parser.MoveNext())
        {
            var current = parser.Current;
            if (current is AnchorAlias || current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw new SemanticInputException("PolicyAliasesOrTags", "YAML aliases, anchors and explicit tags are not accepted.");
            if (current is DocumentStart && ++documents > 1)
                throw new SemanticInputException("PolicyDocumentCount", "Exactly one YAML policy document is required.");
            if (current is MappingStart or SequenceStart && ++depth > MaxDepth)
                throw new SemanticInputException("InputDepth", "Input exceeds the nesting limit of 32.");
            if (current is MappingEnd or SequenceEnd) depth--;
        }
        var policy = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking().WithMaximumRecursion(MaxDepth)
            .WithEnforceNullability().WithEnforceRequiredMembers().Build()
            .Deserialize<PersistencePolicy>(text)
            ?? throw new SemanticInputException("EmptyPolicy", "A persistence policy document is required.");
        if (policy.Entities.Any(p => p.Value is null) || policy.Relationships.Any(p => p.Value is null)
            || policy.Entities.Values.Any(n => n.Properties.Any(p => p.Value is null) || n.KeyProperties.Any(p => p is null))
            || policy.Relationships.Values.Any(e => e.Properties.Any(p => p.Value is null)
                || e.Source.ForeignKeyProperties.Any(p => p is null) || e.Target.ForeignKeyProperties.Any(p => p is null)))
            throw new SemanticInputException("NullPolicyEntry", "Policy collections cannot contain null entries.");
        return policy;
    }

    internal static void Bound(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxBytes || Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new SemanticInputException("InputSize", "Each input is limited to 1 MiB of UTF-8 text.");
    }

    // Resource guard only; all syntax and IRI validation is delegated to the real Turtle parser.
    private static void CheckTurtleDepth(string text)
    {
        var depth = 0;
        char quote = '\0';
        var longQuote = false;
        var iri = false;
        var comment = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (comment) { if (c is '\r' or '\n') comment = false; continue; }
            if (c == '\\') { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote && (!longQuote || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                { if (longQuote) i += 2; quote = '\0'; }
                continue;
            }
            if (iri) { if (c == '>') iri = false; continue; }
            if (c == '#') { comment = true; continue; }
            if (c == '<') { iri = true; continue; }
            if (c is '\'' or '"')
            {
                quote = c;
                longQuote = i + 2 < text.Length && text[i + 1] == c && text[i + 2] == c;
                if (longQuote) i += 2;
                continue;
            }
            if (c is '[' or '(' && ++depth > MaxDepth)
                throw new SemanticInputException("InputDepth", "Input exceeds the nesting limit of 32.");
            if (c is ']' or ')') depth--;
        }
    }

    private sealed class BoundedGraphHandler(IGraph graph) : GraphHandler(graph)
    {
        private int _count;
        protected override bool HandleTripleInternal(Triple triple)
        {
            if (++_count > MaxTriples)
                throw new SemanticInputException("InputTriples", "Each RDF input is limited to 20000 parsed triples.");
            return base.HandleTripleInternal(triple);
        }
    }
}

internal sealed class SemanticInputException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class PersistencePolicy
{
    public required int ContractVersion { get; set; }
    public required string Namespace { get; set; }
    public required string ContextName { get; set; }
    public required Dictionary<string, NodePolicy> Entities { get; set; }
    public required Dictionary<string, EdgePolicy> Relationships { get; set; }
}
internal sealed class NodePolicy
{
    public required string ClrType { get; set; }
    public required string Storage { get; set; }
    public required string Label { get; set; }
    public required string Deletion { get; set; }
    public required List<string> KeyProperties { get; set; }
    public required Dictionary<string, PropertyPolicy> Properties { get; set; }
}
internal sealed class EdgePolicy
{
    public required string ClrType { get; set; }
    public required string Storage { get; set; }
    public required string RelationshipType { get; set; }
    public required string Direction { get; set; }
    public required string KeyProperty { get; set; }
    public required EndpointPolicy Source { get; set; }
    public required EndpointPolicy Target { get; set; }
    public required Dictionary<string, PropertyPolicy> Properties { get; set; }
}
internal sealed class EndpointPolicy
{
    public required string Entity { get; set; }
    public required string Navigation { get; set; }
    public required List<string> ForeignKeyProperties { get; set; }
    public required string Deletion { get; set; }
}
internal sealed class PropertyPolicy
{
    public string? Iri { get; set; }
    public required string StoreName { get; set; }
    public required string Type { get; set; }
    public required bool Required { get; set; }
    public required string Conversion { get; set; }
    public string Concurrency { get; set; } = "none";
}
