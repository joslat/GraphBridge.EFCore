namespace GraphBridge.EntityFrameworkCore.Diagnostics;

/// <summary>Builds every LINQ translation refusal: the refused shape in LINQ terms, a stable GBQ code (in
/// <see cref="Neo4jQueryTranslationException.Code"/>, the message and <c>Exception.Data["GraphBridge.QueryDiagnostic"]</c>)
/// and a supported alternative.</summary>
internal static class Neo4jQueryRefusals
{
    internal const string DataKey = "GraphBridge.QueryDiagnostic";

    private const string Joins = "Match the relationship in Fluent Cypher (db.Cypher()), or load each side with its own query and join a bounded result in memory.";
    private const string Sets = "Run the queries separately and combine bounded results in memory, or use a reviewed native query.";
    private const string Aggregates = "Count/LongCount, Any and Min/Max over a direct int or long property are translated; for other aggregates use Fluent Cypher (Graph.Sum, Avg, Min, Max) or a reviewed native query.";
    private const string Paging = "Apply Where, OrderBy and other operators before Skip/Take, or split the query; combining them would need a Cypher subquery.";
    private const string Values = "Compare mapped scalar properties with variables or constants; compute other values before the query. Methods over entity members are not translated.";
    private const string Includes = "Include one direct native-reference navigation, or load related entities with a second query by key.";
    private const string Projections = "Project mapped scalar properties, optionally into a DTO, or load the entities and shape them in memory.";
    private const string Bulk = "ExecuteUpdateAsync and ExecuteDeleteAsync accept direct scalar setters on a mapped entity root (see the bulk mutations guide).";
    private const string Integrity = "Aggregate over the related node set instead, or use a reviewed native query.";
    private const string Fallback = "Only the documented scalar node/relationship query subset is supported; no client predicate evaluation is performed.";

    // First match wins. Operator overrides arrive as EF method names (TranslateX) and are reported in LINQ terms.
    private static readonly (Func<string, bool> Matches, string Code, string? Shape, string Alternative)[] Rules =
    [
        (Is("TranslateGroupBy"), "GBQ101", "GroupBy", "Group in Fluent Cypher with With(...) and Graph.Count, Min, Max, Sum or Avg, or group a bounded list in memory."),
        (Is("TranslateDistinct"), "GBQ102", "Distinct", "Use ReturnDistinct in Fluent Cypher, or remove duplicates from a bounded projected list in memory."),
        (Is("TranslateJoin"), "GBQ103", "Join", Joins),
        (Is("TranslateGroupJoin"), "GBQ103", "GroupJoin", Joins),
        (Is("TranslateLeftJoin"), "GBQ103", "LeftJoin", Joins),
        (Is("TranslateRightJoin"), "GBQ103", "RightJoin", Joins),
        (Is("TranslateSelectMany"), "GBQ103", "SelectMany", Joins),
        (Is("TranslateUnion"), "GBQ104", "Union", Sets),
        (Is("TranslateConcat"), "GBQ104", "Concat", Sets),
        (Is("TranslateIntersect"), "GBQ104", "Intersect", Sets),
        (Is("TranslateExcept"), "GBQ104", "Except", Sets),
        (Is("TranslateSum"), "GBQ105", "Sum", Aggregates),
        (Is("TranslateAverage"), "GBQ105", "Average", Aggregates),
        (Starts("Min/Max"), "GBQ105", null, Aggregates),
        (Is("TranslateLastOrDefault"), "GBQ106", "Last or LastOrDefault", "Order in the opposite direction and use First or FirstOrDefault."),
        (Is("TranslateReverse"), "GBQ106", "Reverse", "Order in the opposite direction instead."),
        (Is("TranslateElementAtOrDefault"), "GBQ106", "ElementAt or ElementAtOrDefault", "Use OrderBy, then Skip(n) and First or FirstOrDefault."),
        (Is("TranslateSkipWhile"), "GBQ106", "SkipWhile", "Filter with Where instead."),
        (Is("TranslateTakeWhile"), "GBQ106", "TakeWhile", "Filter with Where instead."),
        (Is("TranslateOfType"), "GBQ107", "OfType", "Query the mapped type's own DbSet; each mapped type has its own label."),
        (Is("TranslateCast"), "GBQ107", "Cast", "Query the mapped type's own DbSet; each mapped type has its own label."),
        (Is("TranslateAll"), "GBQ108", "All", "Use Any with the opposite condition, after checking how missing properties should count, or a reviewed native query."),
        (Is("TranslateDefaultIfEmpty"), "GBQ108", "DefaultIfEmpty", "Use OPTIONAL MATCH in Fluent Cypher, or handle an empty result in memory."),
        (s => s == "An aggregate after Skip or Take", "GBQ110", null,
            "Load the page and aggregate it in memory, or use a reviewed native query; aggregating before Skip/Take would change the result."),
        (s => s == "Repeated Take" || s.EndsWith(" after Skip or Take", StringComparison.Ordinal), "GBQ110", null, Paging),
        (s => s == "Subqueries" || s.StartsWith("This operator after scalar projection", StringComparison.Ordinal), "GBQ110", null,
            "Split the query, or apply the operator before the projection; this composition would need a Cypher subquery."),
        (s => s.Contains("aggregates require preservation", StringComparison.Ordinal), "GBQ170", null, Integrity),
        (Starts("Contains"), "GBQ130", null, "Use Contains on a captured array or List<T> of string, int, long, bool, Guid or enum values without explicit converters (at most 1,024)."),
        (s => s.StartsWith("Bulk", StringComparison.Ordinal) || s.StartsWith("Duplicate bulk", StringComparison.Ordinal)
              || s.StartsWith("a bulk setter", StringComparison.Ordinal) || s.StartsWith("Unknown bulk", StringComparison.Ordinal), "GBQ160", null, Bulk),
        (s => s.StartsWith("Projection", StringComparison.Ordinal) || s.StartsWith("Non-identity projections", StringComparison.Ordinal)
              || s.StartsWith("Collection or nested member", StringComparison.Ordinal), "GBQ150", null, Projections),
        // Anchored: Include refusals start or end with the word; other messages may merely mention it.
        (s => s.StartsWith("Include", StringComparison.Ordinal) || s.StartsWith("a reference Include", StringComparison.Ordinal)
              || s.EndsWith(" Include", StringComparison.Ordinal) || s.EndsWith(" Includes", StringComparison.Ordinal), "GBQ140", null, Includes),
        (s => s.StartsWith("Predicate", StringComparison.Ordinal) || s.StartsWith("Value expression", StringComparison.Ordinal)
              || s.StartsWith("the method ", StringComparison.Ordinal) || s.StartsWith("object.Equals", StringComparison.Ordinal)
              || s.StartsWith("Guid construction", StringComparison.Ordinal) || s.StartsWith("Comparisons between", StringComparison.Ordinal)
              || s.StartsWith("Range comparisons", StringComparison.Ordinal) || s.StartsWith("Ordering", StringComparison.Ordinal), "GBQ120", null, Values),
    ];

    internal static Neo4jQueryTranslationException Create(string feature, string? alternative = null)
    {
        var (code, shape, rule) = Classify(feature);
        return new Neo4jQueryTranslationException($"GraphBridge cannot translate {shape} [{code}]. {alternative ?? rule}", code);
    }

    internal static (string Code, string Shape, string Alternative) Classify(string feature)
    {
        foreach (var (matches, code, shape, alternative) in Rules)
            if (matches(feature)) return (code, shape ?? feature, alternative);
        return ("GBQ100", feature, Fallback);
    }

    private static Func<string, bool> Is(string name) => s => s == name;
    private static Func<string, bool> Starts(string prefix) => s => s.StartsWith(prefix, StringComparison.Ordinal);
}
