using System.Text;
using GraphBridge.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

internal sealed partial class CypherCompiler
{
    private void AppendWhere(StringBuilder text, CypherTerm? predicate)
    {
        if (predicate is not null) text.AppendLine().Append("WHERE ").Append(Render(predicate));
    }
    private string Render(CypherTerm term) => term switch
    {
        CypherPropertyTerm property => Property(property).Text,
        CypherAliasTerm alias => Name(alias.Alias),
        CypherLiteralTerm literal => Parameter(literal.Value is int value ? (long)value : literal.Value),
        CypherComparisonTerm comparison => Comparison(comparison),
        CypherBooleanTerm boolean => Boolean(boolean),
        CypherNullTerm nullable => Render(nullable.Operand) + (nullable.Negated ? " IS NOT NULL" : " IS NULL"),
        CypherNotTerm not => Not(not),
        CypherInTerm inList => In(inList),
        CypherStringTerm text => Text(text),
        CypherExistsTerm exists => Exists(exists),
        _ => throw Unsupported("GBFC304", "This expression is not supported in the current clause.")
    };

    private string Comparison(CypherComparisonTerm comparison)
    {
        if (comparison.ClrSemantics) ValidateAgreement(comparison);
        var left = Render(comparison.Left);
        string right;
        if (comparison.Left is CypherPropertyTerm property && comparison.Right is CypherLiteralTerm literal)
        {
            var binding = Property(property).Binding;
            right = Parameter(literal.Value is null ? null : Store(binding, literal.Value));
        }
        else
        {
            // A WITH value is compared as stored; only a converter that keeps the numeric value is comparable with it.
            foreach (var (side, other) in new[] { (comparison.Left, comparison.Right), (comparison.Right, comparison.Left) })
                if (other is CypherAliasTerm && side is CypherPropertyTerm sideProperty
                    && Property(sideProperty).Binding.Property.GetTypeMapping().Converter is { } converter
                    && !Neo4jTypeMappingSource.PreservesOrder(converter))
                    throw CypherDiagnostics.Create("GBFC111", $"Comparing converted property '{Describe(side)}' with a WITH value is not translated: the property is stored in its converted form. Use an unconverted representation or a reviewed native query.");
            if (comparison.Left is CypherPropertyTerm a && comparison.Right is CypherPropertyTerm b)
            {
                var first = Property(a).Binding.Property;
                var second = Property(b).Binding.Property;
                if (first != second && (first.GetTypeMapping().Converter is not null || second.GetTypeMapping().Converter is not null))
                    throw Unsupported("GBFC113", "Comparing different converted properties requires an explicit native query; converter compatibility is not inferred.");
            }
            // A min/max WITH value holds the stored form of its property; compare it with stored values too.
            right = comparison.Left is CypherAliasTerm { Alias: var value } && comparison.Right is CypherLiteralTerm { Value: { } stored }
                && _symbols.TryGetValue(value, out var symbol) && symbol is ValueSymbol { Binding: { } aggregated, List: false }
                ? Parameter(Store(aggregated, stored)) : Render(comparison.Right);
        }
        return left + " " + comparison.Operator + " " + right;
    }

    private string Not(CypherNotTerm not)
    {
        // C# negation turns false into true; native NOT keeps null as null. They agree only where the operand is never null.
        if (not.ClrSemantics && !TwoValued(not.Operand))
            throw CypherDiagnostics.Create("GBFC104", "'!' is translated only over predicates that can never be null here: null tests, EXISTS, and comparisons, IN and string operators whose operands are non-nullable properties of MATCH-bound symbols or non-null values. Use .Not() on an explicit predicate for native semantics, or add explicit null tests.");
        return not.Operand is CypherExistsTerm ? "NOT " + Render(not.Operand) : "NOT (" + Render(not.Operand) + ")";
    }

    /// <summary>FC2-11: a chain of one operator renders flat (a AND b AND c); a child of the other operator keeps its
    /// parentheses. Comparisons bind tighter than NOT, AND and OR in Cypher, so they need none.</summary>
    private string Boolean(CypherBooleanTerm boolean)
    {
        var operands = new List<CypherTerm>();
        Flatten(boolean);
        return string.Join(" " + boolean.Operator + " ", operands.Select(o => o is CypherBooleanTerm ? "(" + Render(o) + ")" : Render(o)));

        void Flatten(CypherTerm term)
        {
            if (term is CypherBooleanTerm same && same.Operator == boolean.Operator) { Flatten(same.Left); Flatten(same.Right); }
            else operands.Add(term);
        }
    }

    /// <summary>True when the native predicate can only be true or false, never null, in the current scope.</summary>
    private bool TwoValued(CypherTerm term) => term switch
    {
        CypherNullTerm or CypherExistsTerm => true,
        CypherBooleanTerm boolean => TwoValued(boolean.Left) && TwoValued(boolean.Right),
        CypherNotTerm not => TwoValued(not.Operand),
        CypherComparisonTerm comparison => NeverNull(comparison.Left) && NeverNull(comparison.Right),
        CypherInTerm inList => NeverNull(inList.Operand) && (inList.ClrSemantics || inList.Values.All(value => value is not null)),
        CypherStringTerm text => NeverNull(text.Operand) && NeverNull(text.Argument),
        _ => false
    };

    private string In(CypherInTerm inList)
    {
        if (inList.Values.Count > MaxListValues)
            throw CypherDiagnostics.Create("GBFC107", $"The IN list has {inList.Values.Count} values; Fluent sends at most {MaxListValues}, like LINQ Contains. Query in batches or use a reviewed native query.");
        var elements = inList.Values;
        if (inList.ClrSemantics && elements.Contains(null))
        {
            // C# Contains(null) is true for a null member; native IN returns null. A member that is never null cannot
            // equal the null element in either language, so it is dropped; otherwise no single native form agrees.
            if (!NeverNull(inList.Operand))
                throw CypherDiagnostics.Create("GBFC110", $"The values contain null and '{Describe(inList.Operand)}' can be null here: C# Contains(null) is true, native IN returns null. Remove null from the values and add the null test explicitly: values.Contains(x) || x == null.");
            elements = elements.Where(value => value is not null).ToArray();
        }
        var binding = inList.Operand is CypherPropertyTerm property ? Property(property).Binding : null;
        var left = Render(inList.Operand);
        var values = elements.Select(value => value is null ? null
            : binding is not null ? Store(binding, value) : value is int integer ? (long)integer : value).ToList();
        return left + " IN " + Parameter(values);
    }

    private string Text(CypherStringTerm text)
    {
        foreach (var operand in new[] { text.Operand, text.Argument })
            if (Converted(operand))
                throw CypherDiagnostics.Create("GBFC111", $"{text.Operator} over converted property '{Describe(operand)}' is not translated: a value converter does not preserve prefixes or substrings. Use an unconverted representation or a reviewed native query.");
        return Render(text.Operand) + " " + text.Operator + " " + Render(text.Argument);
    }

    /// <summary>ADR 0047: a comparison written in C# is emitted only where its native WHERE outcome equals the C# outcome.
    /// Under AND/OR, native null and C# false both drop a row; they differ only where C# turns a null comparison into true.</summary>
    private void ValidateAgreement(CypherComparisonTerm comparison)
    {
        if (comparison.Operator is not ("=" or "<>") && (Unordered(comparison.Left) || Unordered(comparison.Right)))
            throw CypherDiagnostics.Create("GBFC111", $"Ordering comparison over converted property '{Describe(Unordered(comparison.Left) ? comparison.Left : comparison.Right)}' is not translated: this value converter does not prove order preservation. Use an unconverted representation or a reviewed native query.");
        var left = NeverNull(comparison.Left);
        var right = NeverNull(comparison.Right);
        var value = comparison.Right is CypherLiteralTerm;
        if (comparison.Operator == "<>" && !(left && right))
            throw CypherDiagnostics.Create("GBFC110", $"'{Describe(left ? comparison.Right : comparison.Left)}' can be null here (nullable property or OPTIONAL MATCH symbol), so C# '!=' would keep rows that native Cypher drops. Use .Ne(...) for native semantics, or IsNull().Or(....Ne(...)) for the C# meaning.");
        if (comparison.Operator == "=" && !value && !left && !right)
            throw CypherDiagnostics.Create("GBFC110", $"'{Describe(comparison.Left)}' and '{Describe(comparison.Right)}' can both be null here; C# treats null == null as true and native Cypher does not. Combine explicit IsNotNull()/IsNull() predicates with .Eq(...) to state the intended meaning.");
    }

    private bool NeverNull(CypherTerm term) => term switch
    {
        CypherLiteralTerm literal => literal.Value is not null,
        // Counts introduced by WITH are never null.
        CypherAliasTerm { Alias.Definition: CypherCountTerm or CypherAggregateTerm { Function: "sum" } } => true,
        CypherPropertyTerm property => !_optional.Contains(property.Alias) && !Property(property).Binding.Property.IsNullable,
        _ => false
    };

    /// <summary>Converts a compared model value to its stored form; a value the store type cannot hold stays a coded refusal.</summary>
    private static object? Store(Metadata.GraphPropertyBinding binding, object value)
    {
        try { return binding.ToStore(value); }
        catch (OverflowException)
        {
            throw CypherDiagnostics.Create("GBFC105", $"The compared value cannot be stored as property '{binding.Property.Name}' (overflow). Compare a value within the stored range.");
        }
    }

    private bool Converted(CypherTerm term)
        => term is CypherPropertyTerm property && Property(property).Binding.Property.GetTypeMapping().Converter is not null;

    // GraphBridge's own enum-to-Int64 converter is monotonic, so enum ordering agrees with C#.
    private bool Unordered(CypherTerm term)
        => term is CypherPropertyTerm property && Property(property).Binding.Property.GetTypeMapping().Converter is { } converter
            && !Neo4jTypeMappingSource.PreservesOrder(converter);

    private static string Describe(CypherTerm term) => term switch
    {
        CypherPropertyTerm property => property.Property.Name,
        CypherAliasTerm => "WITH value",
        _ => "value"
    };

    private (string Expression, ValueSymbol Symbol) RenderAggregate(CypherAggregateTerm aggregate, string name)
    {
        if (aggregate is { Function: "collect", Operand: CypherAliasTerm { Alias.Kind: CypherAliasKind.Node or CypherAliasKind.Edge } symbol })
        {
            // ADR 0049: nulls from OPTIONAL MATCH are skipped because a map projection over null is null.
            var entity = EntityReader(symbol.Alias);
            return ("collect(" + (aggregate.Distinct ? "DISTINCT " : "") + entity.Render(Name(symbol.Alias)) + ")[.." + Parameter((long)aggregate.Max!) + "]",
                new ValueSymbol(name, List: true, Entity: entity));
        }
        if (aggregate.Operand is not CypherPropertyTerm property)
            throw Unsupported("GBFC406", $"{aggregate.Function} aggregates a mapped scalar property expression, such as alias.Property(p => p.Year).");
        var binding = Property(property).Binding;
        if (aggregate.Function != "collect" && binding.Property.GetTypeMapping().Converter is not null)
            throw CypherDiagnostics.Create("GBFC406", $"{aggregate.Function} over converted property '{property.Property.Name}' is not translated: the server would aggregate the stored form. Use an unconverted property or a reviewed native query.");
        var text = aggregate.Function + "(" + (aggregate.Distinct ? "DISTINCT " : "") + Render(property) + ")";
        if (aggregate.Max is { } max) text += "[.." + Parameter((long)max) + "]";
        var typed = aggregate.Function is "min" or "max" or "collect";
        return (text, new ValueSymbol(name, typed ? binding : null, aggregate.Function == "collect"));
    }

    private string RenderCount(CypherCountTerm count)
        => "count(" + (count.Distinct ? "DISTINCT " : "") + (count.Operand is null ? "*" : Render(count.Operand)) + ")";

    private string Exists(CypherExistsTerm exists)
    {
        if (Capturing) _snapshotStatus = CypherSnapshotStatus.NestedExistsNotRepresented;
        var outer = _scope;
        var outerSymbols = _symbols;
        _scope = [.. outer];
        // Bindings are immutable. Locals belong to this lexical expansion, not the reusable predicate.
        _symbols = new(outerSymbols);
        try
        {
            var text = new StringBuilder("EXISTS { MATCH ").Append(RenderPattern(exists.Pattern));
            AppendWhere(text, exists.Predicate);
            return text.Append(" }").ToString();
        }
        finally { _scope = outer; _symbols = outerSymbols; }
    }
}
