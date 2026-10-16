using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.Tooling.Generation;
using VDS.RDF;
using VDS.RDF.Parsing;
using YamlDotNet.Core;

namespace GraphBridge.Tooling.Semantics;

public sealed class OntologyCompilationResult
{
    internal OntologyCompilationResult(GraphGenerationModel? model, IEnumerable<GraphGenerationDiagnostic> diagnostics, string sourceFingerprint)
    {
        Diagnostics = SemanticDiagnostics.Order(diagnostics);
        Model = Diagnostics.Any(d => d.Severity == GraphGenerationSeverity.Error) ? null : model;
        SourceFingerprint = sourceFingerprint;
    }
    public GraphGenerationModel? Model { get; }
    public bool Success => Model is not null;
    public IReadOnlyList<GraphGenerationDiagnostic> Diagnostics { get; }
    /// <summary>SHA256 of ordered inputs with line endings normalized to LF; empty when input bounds reject hashing. Never a runtime storage fingerprint.</summary>
    public string SourceFingerprint { get; }
}

/// <summary>Offline declared-vocabulary and explicit-policy compilation. No OWL reasoning or runtime model authority.</summary>
public static class OntologyCompiler
{
    public static OntologyCompilationResult Compile(string ontologyText, string policyYaml, string? shapesText = null)
    {
        ArgumentNullException.ThrowIfNull(ontologyText);
        ArgumentNullException.ThrowIfNull(policyYaml);
        var fingerprint = "";
        var diagnostics = new List<GraphGenerationDiagnostic>();
        try
        {
            SemanticsInput.Bound(ontologyText);
            SemanticsInput.Bound(policyYaml);
            if (shapesText is not null) SemanticsInput.Bound(shapesText);
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { Normalize(ontologyText), Normalize(policyYaml), shapesText is null ? null : Normalize(shapesText) })))).ToLowerInvariant();
            using var graph = SemanticsInput.Turtle(ontologyText);
            var policy = SemanticsInput.Policy(policyYaml);
            var model = new Resolver(graph, policy, diagnostics).Resolve();
            if (shapesText is not null)
            {
                using var shapes = SemanticsInput.Turtle(shapesText);
                ShaclContract.Check(shapes, diagnostics);
                diagnostics.Add(new("ShaclSeparateValidation", GraphGenerationSeverity.Info, "shapes",
                    "SHACL is retained for separate RDF data validation; no shape constraint is silently promoted to EF or database enforcement."));
            }
            return new(model, diagnostics, fingerprint);
        }
        catch (SemanticInputException exception) { diagnostics.Add(SemanticDiagnostics.Error(exception.Code, "input", exception.Message)); }
        catch (YamlException exception) { diagnostics.Add(SemanticDiagnostics.Error("InvalidPolicyYaml", "policy", $"Invalid or incomplete strict YAML policy at line {exception.Start.Line}, column {exception.Start.Column}.")); }
        catch (RdfParseException) { diagnostics.Add(SemanticDiagnostics.Error("InvalidTurtle", "input", "Input is not valid bounded W3C Turtle with valid IRIs.")); }
        return new(null, diagnostics, fingerprint);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed class Resolver(Graph graph, PersistencePolicy policy, List<GraphGenerationDiagnostic> diagnostics)
    {
        private readonly Dictionary<string, GraphNodeDefinition> _nodes = new(StringComparer.Ordinal);
        private readonly List<GraphIdentifiedEdgeDefinition> _edges = [];
        private readonly HashSet<string> _mapped = new(StringComparer.Ordinal);

        internal GraphGenerationModel Resolve()
        {
            if (policy.ContractVersion != 1) Error("PolicyVersion", "policy", "Only persistence contractVersion 1 is supported.");
            if (string.IsNullOrWhiteSpace(policy.Namespace) || string.IsNullOrWhiteSpace(policy.ContextName))
                Error("PolicyNames", "policy", "Namespace and contextName must be explicit nonempty names.");
            foreach (var pair in policy.Entities.OrderBy(p => p.Key, StringComparer.Ordinal)) Node(pair.Key, pair.Value);
            foreach (var pair in policy.Relationships.OrderBy(p => p.Key, StringComparer.Ordinal)) Edge(pair.Key, pair.Value);
            AuditOntology();
            diagnostics.Add(new("SemanticBoundary", GraphGenerationSeverity.Info, "ontology",
                "Only explicitly declared classes and properties are inspected. No OWL/RDFS closure is computed; domain/range are inference-bearing statements, not requiredness or closed-world constraints."));
            return new(policy.Namespace, policy.ContextName, _nodes.Values.OrderBy(n => n.Iri, StringComparer.Ordinal),
                _edges.OrderBy(e => e.Iri, StringComparer.Ordinal));
        }

        private void Node(string text, NodePolicy node)
        {
            var iri = Iri(text);
            if (iri is null) return;
            RequireDeclaration(iri, Vocabulary.Owl + "Class", Vocabulary.Rdfs + "Class");
            if (node.Storage != "node") Error("NodeStorage", iri, "Entity storage must explicitly be node.");
            var properties = Properties(node.Properties, iri, nodeOwner: iri);
            var definition = new GraphNodeDefinition(node.ClrType, iri, node.Label, properties, node.KeyProperties, Deletion(node.Deletion, iri));
            if (!_nodes.TryAdd(iri, definition)) Error("DuplicatePolicyIri", iri, "Multiple policy names resolve to the same entity IRI.");
            _mapped.Add(iri);
            Keys(node.KeyProperties, properties, iri);
        }

        private void Edge(string text, EdgePolicy edge)
        {
            var iri = Iri(text);
            if (iri is null) return;
            RequireDeclaration(iri, Vocabulary.Owl + "ObjectProperty");
            if (!_mapped.Add(iri)) Error("DuplicatePolicyIri", iri, "Multiple policy mappings resolve to the same ontology IRI.");
            if (edge.Storage != "identified-edge") Error("EdgeStorage", iri, "Relationship storage must explicitly be identified-edge.");
            if (edge.Direction != "outgoing") Error("EdgeDirection", iri, "Only explicit outgoing source-to-target direction is supported.");
            var properties = Properties(edge.Properties, iri, nodeOwner: null);
            var source = Endpoint(edge.Source, properties, iri);
            var target = Endpoint(edge.Target, properties, iri);
            Keys([edge.KeyProperty], properties, iri);
            if (source is null || target is null) return;
            if (source.ForeignKeyProperties.Concat(target.ForeignKeyProperties).Contains(edge.KeyProperty, StringComparer.Ordinal))
                Error("EdgeIdentityOverlap", iri, "The identified edge key must be separate from both endpoint foreign keys.");
            var sourceIri = Iri(edge.Source.Entity);
            var targetIri = Iri(edge.Target.Entity);
            CheckDomainRange(iri, Vocabulary.Rdfs + "domain", sourceIri);
            CheckDomainRange(iri, Vocabulary.Rdfs + "range", targetIri);
            _edges.Add(new(edge.ClrType, iri, edge.RelationshipType, properties, edge.KeyProperty, source, target, GraphEdgeDirection.SourceToTarget));
            diagnostics.Add(new("IdentifiedEdgeReification", GraphGenerationSeverity.Info, iri,
                "Application edge IDs and payload are explicit policy enrichment. Parallel identified episodes do not preserve the set identity of a single RDF object-property triple."));
        }

        private GraphEndpointDefinition? Endpoint(EndpointPolicy endpoint, IReadOnlyList<GraphPropertyDefinition> properties, string subject)
        {
            var iri = Iri(endpoint.Entity);
            if (iri is null || !_nodes.TryGetValue(iri, out var node))
            { Error("UnmappedEndpoint", subject, "Every endpoint must refer to an explicitly mapped node IRI."); return null; }
            if (endpoint.ForeignKeyProperties.Count != node.KeyProperties.Count)
                Error("EndpointKeyArity", subject, "Endpoint foreign keys must match the principal key's declared order and length.");
            Keys(endpoint.ForeignKeyProperties, properties, subject);
            for (var i = 0; i < Math.Min(endpoint.ForeignKeyProperties.Count, node.KeyProperties.Count); i++)
            {
                var dependent = properties.FirstOrDefault(p => p.ClrName == endpoint.ForeignKeyProperties[i]);
                var principal = node.Properties.FirstOrDefault(p => p.ClrName == node.KeyProperties[i]);
                if (dependent is not null && principal is not null && (dependent.ScalarType != principal.ScalarType || dependent.Conversion != principal.Conversion))
                    Error("EndpointKeyType", subject, "Each ordered foreign-key slot must use its principal key's scalar type and conversion.");
            }
            return new(node.ClrName, endpoint.Navigation, endpoint.ForeignKeyProperties, Deletion(endpoint.Deletion, subject));
        }

        private IReadOnlyList<GraphPropertyDefinition> Properties(Dictionary<string, PropertyPolicy> entries, string subject, string? nodeOwner)
        {
            var result = new List<GraphPropertyDefinition>();
            foreach (var pair in entries.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var entry = pair.Value;
                var location = subject + "/" + pair.Key;
                var iri = entry.Iri is null ? null : Iri(entry.Iri);
                var type = Scalar(entry.Type, location);
                var conversion = Conversion(entry.Conversion, location);
                if (type == GraphScalarType.Guid ? conversion != GraphScalarConversion.GuidString
                    : type == GraphScalarType.DateOnly ? conversion != GraphScalarConversion.DateOnlyIsoString
                    : conversion != GraphScalarConversion.Native)
                    Error("ScalarConversion", location, "Guid requires guid-string, DateOnly requires date-only-iso-string, and other supported scalar types require native conversion.");
                var concurrency = entry.Concurrency switch
                {
                    "none" => GraphConcurrencyMode.None,
                    "original-value" => GraphConcurrencyMode.OriginalValue,
                    "incrementing-int64" => GraphConcurrencyMode.IncrementingInt64,
                    _ => InvalidConcurrency(location)
                };
                if (concurrency == GraphConcurrencyMode.IncrementingInt64 && (type != GraphScalarType.Int64 || !entry.Required))
                    Error("VersionType", location, "Incrementing concurrency requires a required Int64 property.");
                if (iri is not null)
                {
                    RequireDeclaration(iri, Vocabulary.Owl + "DatatypeProperty");
                    _mapped.Add(iri);
                    if (nodeOwner is not null) CheckDomainRange(iri, Vocabulary.Rdfs + "domain", nodeOwner);
                    var ranges = Objects(iri, Vocabulary.Rdfs + "range");
                    if (ranges.Count > 1) Error("AmbiguousDomainRange", iri, "Multiple domain/range statements denote intersecting inference conditions; this compiler does not choose one.");
                    else if (ranges.Count == 1 && !CompatibleDatatype(ranges[0], type))
                        Error("DatatypePolicyMismatch", iri, "The declared datatype range and selected policy scalar are not in the supported compatibility table.");
                }
                else diagnostics.Add(new("PolicyPropertyEnrichment", GraphGenerationSeverity.Info, location,
                    "This property has no ontology IRI; it is explicitly supplied by persistence policy."));
                if (type == GraphScalarType.DateOnly)
                    diagnostics.Add(new("CalendarDateConversion", GraphGenerationSeverity.Info, location,
                        "DateOnly stores an invariant yyyy-MM-dd calendar date. Time zones, instants and arbitrary xsd:date lexical forms are not preserved."));
                result.Add(new(pair.Key, iri, entry.StoreName, type, entry.Required, conversion, concurrency));
            }
            return result.AsReadOnly();
        }

        private void Keys(IReadOnlyList<string> keys, IReadOnlyList<GraphPropertyDefinition> properties, string subject)
        {
            if (keys.Count == 0 || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                Error("ExplicitIdentity", subject, "Identity/endpoint key slots must be explicit, nonempty and distinct.");
            foreach (var key in keys)
            {
                var property = properties.FirstOrDefault(p => p.ClrName == key);
                if (property is null || !property.IsRequired)
                    Error("RequiredKeyProperty", subject + "/" + key, "Every key or endpoint slot must name a declared required property.");
            }
        }

        private GraphScalarType Scalar(string value, string subject) => value switch
        {
            "string" => GraphScalarType.String, "boolean" => GraphScalarType.Boolean,
            "int32" => GraphScalarType.Int32, "int64" => GraphScalarType.Int64,
            "double" => GraphScalarType.Double, "guid" => GraphScalarType.Guid,
            "date-only" => GraphScalarType.DateOnly,
            _ => InvalidScalar(subject)
        };
        private GraphScalarType InvalidScalar(string subject) { Error("UnsupportedScalar", subject, "Unsupported explicit scalar type."); return GraphScalarType.String; }
        private GraphScalarConversion Conversion(string value, string subject) => value switch
        {
            "native" => GraphScalarConversion.Native, "guid-string" => GraphScalarConversion.GuidString,
            "date-only-iso-string" => GraphScalarConversion.DateOnlyIsoString,
            _ => InvalidConversion(subject)
        };
        private GraphScalarConversion InvalidConversion(string subject) { Error("UnsupportedConversion", subject, "Only named built-in conversion policies are accepted; no executable expressions."); return GraphScalarConversion.Native; }
        private GraphConcurrencyMode InvalidConcurrency(string subject) { Error("UnsupportedConcurrency", subject, "Unsupported explicit concurrency policy."); return GraphConcurrencyMode.None; }
        private GraphDeletionPolicy Deletion(string value, string subject)
        {
            if (value != "restrict") Error("DeletionPolicy", subject, "Only explicit restrict deletion is supported.");
            return GraphDeletionPolicy.Restrict;
        }

        private void CheckDomainRange(string iri, string predicate, string? expected)
        {
            var values = Objects(iri, predicate);
            if (values.Count > 1) Error("AmbiguousDomainRange", iri, "Multiple domain/range statements denote intersecting inference conditions; this compiler does not choose one.");
            else if (values.Count == 1 && values[0] != expected)
                Error("EndpointPolicyMismatch", iri, "The explicit mapping differs from the named declared domain/range; inference or reinterpretation is not performed.");
        }
        private List<string> Objects(string subject, string predicate) => graph.Triples
            .Where(t => Vocabulary.Uri(t.Subject) == subject && Vocabulary.Uri(t.Predicate) == predicate)
            .Select(t => Vocabulary.Uri(t.Object) ?? "<anonymous-or-literal>").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        private void RequireDeclaration(string iri, params string[] types)
        {
            if (!graph.Triples.Any(t => Vocabulary.Uri(t.Subject) == iri && Vocabulary.Uri(t.Predicate) == Vocabulary.Rdf + "type" && types.Contains(Vocabulary.Uri(t.Object), StringComparer.Ordinal)))
                Error("MissingDeclaration", iri, "The mapped IRI requires an explicit supported class/property declaration.");
        }

        private string? Iri(string value)
        {
            if ((value.StartsWith("https://", StringComparison.Ordinal) || value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("urn:", StringComparison.Ordinal))
                && System.Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri.AbsoluteUri;
            var colon = value.IndexOf(':');
            if (colon >= 0 && graph.NamespaceMap.HasNamespace(value[..colon])
                && System.Uri.TryCreate(graph.NamespaceMap.GetNamespaceUri(value[..colon]).AbsoluteUri + value[(colon + 1)..], UriKind.Absolute, out var expanded))
                return expanded.AbsoluteUri;
            Error("UnresolvedPolicyIri", value, "Use an ontology-declared QName or an absolute HTTP(S)/URN IRI.");
            return null;
        }

        private void AuditOntology()
        {
            foreach (var triple in graph.Triples)
            {
                var subject = Vocabulary.Uri(triple.Subject) ?? "<anonymous>";
                var predicate = Vocabulary.Uri(triple.Predicate) ?? "<anonymous>";
                var value = Vocabulary.Uri(triple.Object);
                var supportedType = predicate == Vocabulary.Rdf + "type" && value is not null
                    && new[] { Vocabulary.Owl + "Class", Vocabulary.Rdfs + "Class", Vocabulary.Owl + "ObjectProperty", Vocabulary.Owl + "DatatypeProperty", Vocabulary.Owl + "Ontology" }.Contains(value, StringComparer.Ordinal);
                var annotation = predicate is Vocabulary.Rdfs + "label" or Vocabulary.Rdfs + "comment" or Vocabulary.Owl + "versionInfo";
                var domainRange = predicate is Vocabulary.Rdfs + "domain" or Vocabulary.Rdfs + "range";
                if (supportedType && subject != "<anonymous>" && value != Vocabulary.Owl + "Ontology" && !_mapped.Contains(subject))
                    diagnostics.Add(new("UnmappedConcept", GraphGenerationSeverity.Warning, subject, "Declared concept is not included by the explicit persistence policy."));
                if (supportedType && subject != "<anonymous>" || annotation || domainRange && value is not null) continue;
                diagnostics.Add(new("UnsupportedOntologyConstruct", GraphGenerationSeverity.Warning, subject,
                    "Uncompiled statement using " + predicate + (predicate == Vocabulary.Rdf + "type" ? " / " + (value ?? "<anonymous-or-literal>") : "")
                    + ". No inference, import fetching or semantic flattening is performed."));
            }
        }

        private static bool CompatibleDatatype(string iri, GraphScalarType type) => type switch
        {
            GraphScalarType.String or GraphScalarType.Guid => iri == Vocabulary.Xsd + "string",
            GraphScalarType.Boolean => iri == Vocabulary.Xsd + "boolean",
            GraphScalarType.Int32 => iri is Vocabulary.Xsd + "int" or Vocabulary.Xsd + "integer",
            GraphScalarType.Int64 => iri is Vocabulary.Xsd + "long" or Vocabulary.Xsd + "integer" or Vocabulary.Xsd + "int",
            GraphScalarType.Double => iri is Vocabulary.Xsd + "double" or Vocabulary.Xsd + "float",
            GraphScalarType.DateOnly => iri == Vocabulary.Xsd + "date",
            _ => false
        };
        private void Error(string code, string subject, string message) => diagnostics.Add(SemanticDiagnostics.Error(code, subject, message));
    }
}

internal static class SemanticDiagnostics
{
    internal static GraphGenerationDiagnostic Error(string code, string subject, string message) => new(code, GraphGenerationSeverity.Error, subject, message);
    internal static IReadOnlyList<GraphGenerationDiagnostic> Order(IEnumerable<GraphGenerationDiagnostic> values)
        => Array.AsReadOnly(values.Distinct().OrderBy(d => d.Subject, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Severity).ThenBy(d => d.Message, StringComparer.Ordinal).ToArray());
}
internal static class Vocabulary
{
    internal const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    internal const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    internal const string Owl = "http://www.w3.org/2002/07/owl#";
    internal const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    internal const string Sh = "http://www.w3.org/ns/shacl#";
    internal static string? Uri(INode node) => (node as IUriNode)?.Uri.AbsoluteUri;
}
