using System.Linq.Expressions;
using System.Reflection;
using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

internal sealed partial class CypherCompiler
{
    private Projection<T> BuildProjection<T>(Expression<Func<T>> projection)
    {
        var columns = new List<ProjectionColumn>();
        var values = Expression.Parameter(typeof(object[]), "values");
        var body = Build(projection.Body);
        var create = Expression.Lambda<Func<object?[], T>>(body, values).Compile();
        return new(columns, row =>
        {
            var materialized = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                if (!row.TryGetValue("c" + i, out var value))
                    throw new InvalidOperationException("Fluent Cypher result is missing projected column c" + i + ".");
                if (value is null)
                {
                    if (columns[i].Type.IsValueType && Nullable.GetUnderlyingType(columns[i].Type) is null)
                        throw new InvalidOperationException("A fluent Cypher projection returned null for a non-nullable value. Project a nullable scalar for OPTIONAL MATCH.");
                    materialized[i] = null;
                }
                else materialized[i] = columns[i].Path is { } path ? path.Read(value)
                    : columns[i].List ? ReadList(value, columns[i])
                    : columns[i].Binding is { } binding ? binding.FromStore(value)
                    // Native sum starts at integer 0, so a double sum over an empty or all-null group arrives as a long.
                    : value is long && (columns[i].Type == typeof(double) || columns[i].Type == typeof(double?))
                        ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : value;
            }
            return create(materialized);
        });

        Expression Build(Expression expression)
        {
            if (TryTerm(expression, out var term)) return Scalar(term, expression.Type);
            // Graph.Is(predicate) returns the predicate's native value; a predicate that can be null needs bool?.
            var operand = expression is UnaryExpression { NodeType: ExpressionType.Convert } wrapped
                && Nullable.GetUnderlyingType(wrapped.Type) == wrapped.Operand.Type ? wrapped.Operand : expression;
            if (CypherPredicateTranslator.TryEmbedded(operand, out var predicate))
            {
                if (expression.Type == typeof(bool) && !TwoValued(predicate))
                    throw Unsupported("GBFC407", "This returned predicate can be null (for example a comparison on a nullable property), so it cannot be read as bool. Project it as (bool?)Graph.Is(...).");
                return Scalar(predicate, expression.Type);
            }
            if (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert
                && Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type
                && TryTerm(convert.Operand, out term))
                return Scalar(term, convert.Type);
            if (expression is NewExpression construction)
            {
                RejectEntity(construction.Type);
                if (construction.Arguments.Count == 0) throw Unsupported("GBFC401", "RETURN must project at least one mapped scalar.");
                return construction.Update(construction.Arguments.Select(Build));
            }
            if (expression is MemberInitExpression initializer)
            {
                RejectEntity(initializer.Type);
                if (initializer.NewExpression.Arguments.Count != 0 || initializer.Bindings.Count == 0
                    || initializer.Bindings.Any(b => b is not MemberAssignment))
                    throw Unsupported("GBFC402", "DTO initializers require a parameterless constructor and scalar member assignments.");
                return Expression.MemberInit(initializer.NewExpression,
                    initializer.Bindings.Cast<MemberAssignment>().Select(b => Expression.Bind(b.Member, Build(b.Expression))));
            }
            throw Unsupported("GBFC403", "RETURN supports mapped scalar Value properties, WITH values and DTO construction only. Method calls, client computations and entity returns are not supported.");
        }
        Expression Scalar(CypherTerm term, Type type)
        {
            var binding = term is CypherPropertyTerm property ? Property(property).Binding : null;
            PathContract? path = null;
            var list = false;
            if (term is CypherAliasTerm aggregate && InlineAggregate(aggregate.Alias))
                // Native implicit grouping: RETURN groups by its non-aggregate columns; no WITH is needed.
                (binding, list) = AggregateReader(aggregate.Alias);
            else if (term is CypherAliasTerm alias)
            {
                Name(alias.Alias);
                if (alias.Alias.Kind is not (CypherAliasKind.Value or CypherAliasKind.Path)) throw Unsupported("GBFC404", "Return scalar properties instead of node or edge entities.");
                path = (_symbols[alias.Alias] as PathSymbol)?.Contract;
                if (_symbols[alias.Alias] is ValueSymbol value) (binding, list) = (value.Binding, value.List);
            }
            var index = columns.FindIndex(c => c.Term == term && c.Type == type);
            if (index < 0) { index = columns.Count; columns.Add(new(term, type, binding, path, list)); }
            return Expression.Convert(Expression.ArrayIndex(values, Expression.Constant(index)), type);
        }
        void RejectEntity(Type type)
        {
            if (_model.GetEntityTypes().Any(e => e.ClrType == type))
                throw Unsupported("GBFC404", "Fluent results are untracked scalar DTOs. Do not construct mapped entity types in RETURN.");
        }
    }

    internal static bool TryTerm(Expression expression, out CypherTerm term)
    {
        if (expression is MemberExpression { Member: PropertyInfo property, Expression: MemberExpression value }
            && value.Member.Name == "Value" && ReadCapture(value.Expression) is CypherAlias alias)
        {
            term = new CypherPropertyTerm(alias, property);
            return true;
        }
        if (expression is MemberExpression { Member.Name: "Value" } member
            && ReadCapture(member.Expression) is CypherAlias valueAlias)
        {
            term = new CypherAliasTerm(valueAlias);
            return true;
        }
        term = null!;
        return false;
    }

    // Read only captured fields, never arbitrary getters or compiled client expressions.
    internal static object? ReadCapture(Expression? expression) => expression switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field } member when member.Expression is not null
            && ReadCapture(member.Expression) is { } receiver && field.DeclaringType!.IsInstanceOfType(receiver)
            => field.GetValue(receiver),
        _ => null
    };

    /// <summary>An aggregate value returned without being introduced by WITH.</summary>
    private bool InlineAggregate(CypherAlias alias)
        => alias.Definition is CypherCountTerm or CypherAggregateTerm && !_symbols.ContainsKey(alias);

    private (GraphPropertyBinding? Binding, bool List) AggregateReader(CypherAlias alias)
        => alias.Definition is CypherAggregateTerm { Operand: CypherPropertyTerm property, Function: "min" or "max" or "collect" } typed
            ? (Property(property).Binding, typed.Function == "collect") : (null, false);

    private string RenderInlineAggregate(CypherAlias alias, string column)
    {
        var text = alias.Definition is CypherCountTerm count ? RenderCount(count)
            : RenderAggregate((CypherAggregateTerm)alias.Definition!, column).Expression;
        if (Capturing) _snapshotAggregates!.Add(new(column, text));
        return text;
    }

    private static object ReadList(object value, ProjectionColumn column)
    {
        var elementType = column.Type.GetGenericArguments()[0];
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var element in (System.Collections.IEnumerable)value)
            list.Add(element is null ? null : column.Binding!.FromStore(element));
        return list;
    }

    private sealed record ProjectionColumn(CypherTerm Term, Type Type, GraphPropertyBinding? Binding, PathContract? Path, bool List = false);
    private sealed record Projection<T>(List<ProjectionColumn> Columns, Func<IReadOnlyDictionary<string, object>, T> Read);
}
