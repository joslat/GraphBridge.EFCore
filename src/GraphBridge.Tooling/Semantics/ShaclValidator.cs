using System.Globalization;
using GraphBridge.Tooling.Generation;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Shacl;

namespace GraphBridge.Tooling.Semantics;

public sealed class ShaclValidationResult
{
    internal ShaclValidationResult(bool? conforms, IEnumerable<GraphGenerationDiagnostic> diagnostics)
    { Conforms = conforms; Diagnostics = SemanticDiagnostics.Order(diagnostics); }
    /// <summary>False when parsing or supported-shape checks prevented validation; this is not a conformance result.</summary>
    public bool Validated => Conforms.HasValue;
    public bool? Conforms { get; }
    public IReadOnlyList<GraphGenerationDiagnostic> Diagnostics { get; }
}

/// <summary>Separate offline RDF validation through dotNetRDF SHACL. It does not validate EF entities or enforce database constraints.</summary>
public static class ShaclValidator
{
    public static ShaclValidationResult Validate(string shapesText, string dataText)
    {
        ArgumentNullException.ThrowIfNull(shapesText);
        ArgumentNullException.ThrowIfNull(dataText);
        var diagnostics = new List<GraphGenerationDiagnostic>();
        try
        {
            using var shapes = SemanticsInput.Turtle(shapesText);
            using var data = SemanticsInput.Turtle(dataText);
            ShaclContract.Check(shapes, diagnostics);
            if (diagnostics.Any(d => d.Severity == GraphGenerationSeverity.Error)) return new(null, diagnostics);
            var report = new ShapesGraph(shapes).Validate(data);
            foreach (var result in report.Results)
            {
                // Blank-node IDs and engine prose are intentionally excluded from deterministic diagnostics.
                var subject = Vocabulary.Uri(result.FocusNode) ?? "<anonymous-or-literal-focus>";
                var component = Vocabulary.Uri(result.SourceConstraintComponent) ?? "<constraint>";
                var path = report.Graph.GetTriplesWithSubjectPredicate(result, report.Graph.CreateUriNode(new Uri(Vocabulary.Sh + "resultPath")))
                    .Select(t => Vocabulary.Uri(t.Object)).FirstOrDefault(value => value is not null);
                diagnostics.Add(new("ShaclViolation", GraphGenerationSeverity.Error, subject,
                    "SHACL constraint " + component + (path is null ? "" : " at " + path) + " did not conform. This is a separate RDF validation result."));
            }
            return new(report.Conforms, diagnostics);
        }
        catch (SemanticInputException exception) { diagnostics.Add(SemanticDiagnostics.Error(exception.Code, "input", exception.Message)); }
        catch (RdfParseException) { diagnostics.Add(SemanticDiagnostics.Error("InvalidTurtle", "input", "Input is not valid bounded W3C Turtle with valid IRIs.")); }
        catch (RdfException) { diagnostics.Add(SemanticDiagnostics.Error("InvalidShacl", "shapes", "The supplied shapes could not be evaluated by the SHACL engine.")); }
        return new(null, diagnostics);
    }
}

internal static class ShaclContract
{
    private static readonly HashSet<string> Predicates = new(StringComparer.Ordinal)
    {
        Vocabulary.Rdf + "type", Vocabulary.Rdfs + "label", Vocabulary.Rdfs + "comment",
        Vocabulary.Sh + "targetClass", Vocabulary.Sh + "property", Vocabulary.Sh + "path",
        Vocabulary.Sh + "minCount", Vocabulary.Sh + "maxCount", Vocabulary.Sh + "datatype",
        Vocabulary.Sh + "class", Vocabulary.Sh + "nodeKind"
    };
    private static readonly HashSet<string> Datatypes = new(StringComparer.Ordinal)
    {
        Vocabulary.Xsd + "string", Vocabulary.Xsd + "boolean", Vocabulary.Xsd + "int", Vocabulary.Xsd + "long",
        Vocabulary.Xsd + "integer", Vocabulary.Xsd + "double", Vocabulary.Xsd + "float", Vocabulary.Xsd + "date"
    };

    internal static void Check(IGraph graph, List<GraphGenerationDiagnostic> diagnostics)
    {
        var rootShapes = graph.Triples.Where(t => Vocabulary.Uri(t.Predicate) == Vocabulary.Rdf + "type" && Vocabulary.Uri(t.Object) == Vocabulary.Sh + "NodeShape")
            .Select(t => t.Subject).Distinct().ToArray();
        var properties = graph.Triples.Where(t => Vocabulary.Uri(t.Predicate) == Vocabulary.Sh + "property").Select(t => t.Object).Distinct().ToArray();
        if (rootShapes.Length == 0) Error("ShaclTargets", "shapes", "At least one explicit sh:NodeShape with sh:targetClass is required.");
        foreach (var shape in rootShapes)
        {
            if (graph.Triples.Any(t => t.Subject.Equals(shape) && Vocabulary.Uri(t.Predicate) == Vocabulary.Rdf + "type"
                && Vocabulary.Uri(t.Object) == Vocabulary.Sh + "PropertyShape"))
                Error("ConflictingShaclKind", Subject(shape), "A shape cannot be both NodeShape and PropertyShape.");
            var targets = Values(shape, "targetClass");
            if (targets.Count == 0 || targets.Any(value => value is not IUriNode))
                Error("ShaclTargets", Subject(shape), "Each root NodeShape requires a named targetClass; a targetless shape is not silently validated.");
            if (Values(shape, "path").Count != 0 || Values(shape, "minCount").Count != 0 || Values(shape, "maxCount").Count != 0)
                Error("ShaclShapePlacement", Subject(shape), "Path and count constraints belong to linked property shapes in the supported subset.");
        }
        foreach (var property in properties)
        {
            var paths = Values(property, "path");
            if (property is not (IBlankNode or IUriNode) || paths.Count != 1 || paths[0] is not IUriNode)
                Error("ShaclPath", Subject(property), "A linked property shape requires one direct named IRI path; compound/inverse paths are unsupported.");
            if (rootShapes.Contains(property) || Values(property, "property").Count != 0)
                Error("RecursiveShaclShape", Subject(property), "Nested or recursive linked shapes are outside the bounded supported subset.");
        }
        foreach (var triple in graph.Triples)
        {
            var predicate = Vocabulary.Uri(triple.Predicate) ?? "<anonymous>";
            var subject = Subject(triple.Subject);
            if (!Predicates.Contains(predicate))
            { Error("UnsupportedShacl", subject, "Unsupported shape predicate " + predicate + "; it is not silently ignored or executed."); continue; }
            if (!rootShapes.Contains(triple.Subject) && !properties.Contains(triple.Subject))
                Error("UnreachableShaclShape", subject, "Shape statements must belong to an explicit targeted NodeShape or its directly linked property shape.");
            if (predicate == Vocabulary.Rdf + "type" && Vocabulary.Uri(triple.Object) is not (Vocabulary.Sh + "NodeShape" or Vocabulary.Sh + "PropertyShape"))
                Error("UnsupportedShacl", subject, "Only explicit NodeShape/PropertyShape declarations are accepted in a shapes document.");
            if (predicate is Vocabulary.Sh + "targetClass" or Vocabulary.Sh + "class" or Vocabulary.Sh + "datatype" or Vocabulary.Sh + "nodeKind")
            {
                if (triple.Object is not IUriNode) Error("InvalidShaclParameter", subject, "Class, datatype and node-kind parameters must be named IRIs.");
                else if (predicate == Vocabulary.Sh + "datatype" && !Datatypes.Contains(Vocabulary.Uri(triple.Object)!))
                    Error("UnsupportedShaclDatatype", subject, "The datatype is outside the explicitly tested scalar validation subset.");
                else if (predicate == Vocabulary.Sh + "nodeKind" && Vocabulary.Uri(triple.Object) is not
                    (Vocabulary.Sh + "IRI" or Vocabulary.Sh + "BlankNode" or Vocabulary.Sh + "Literal" or Vocabulary.Sh + "BlankNodeOrIRI" or Vocabulary.Sh + "BlankNodeOrLiteral" or Vocabulary.Sh + "IRIOrLiteral"))
                    Error("InvalidShaclParameter", subject, "Unknown SHACL nodeKind.");
            }
            if (predicate is Vocabulary.Sh + "minCount" or Vocabulary.Sh + "maxCount")
            {
                if (triple.Object is not ILiteralNode literal || literal.DataType?.AbsoluteUri != Vocabulary.Xsd + "integer"
                    || !long.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
                    Error("InvalidShaclCount", subject, "Count constraints require a nonnegative xsd:integer representable as Int64.");
            }
        }
        foreach (var shape in rootShapes.Concat(properties).Distinct())
        foreach (var name in new[] { "path", "minCount", "maxCount", "datatype", "class", "nodeKind" })
            if (Values(shape, name).Count > 1)
                Error("AmbiguousShaclParameter", Subject(shape), "Multiple values for " + name + " are unsupported; no value is selected arbitrarily.");

        List<INode> Values(INode subject, string name) => graph.Triples.Where(t => t.Subject.Equals(subject) && Vocabulary.Uri(t.Predicate) == Vocabulary.Sh + name).Select(t => t.Object).ToList();
        string Subject(INode subject)
        {
            if (Vocabulary.Uri(subject) is { } iri) return iri;
            var path = Values(subject, "path").Select(Vocabulary.Uri).FirstOrDefault(value => value is not null);
            return path is null ? "<anonymous-shape>" : "property:" + path;
        }
        void Error(string code, string subject, string message) => diagnostics.Add(SemanticDiagnostics.Error(code, subject, message));
    }
}
