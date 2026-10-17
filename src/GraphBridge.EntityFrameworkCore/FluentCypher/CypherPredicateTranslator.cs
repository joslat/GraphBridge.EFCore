using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>Translates C# lambdas into native predicate terms when Where or OrderBy is called (ADR 0047).</summary>
/// <remarks>Nothing in the lambda executes. Symbols are recognized syntactically; captured values are read only from constants
/// and fields, so translation never runs application code. Shape checks run here. Model-dependent agreement checks
/// (nullability, optional symbols, converters) run in the compiler, which knows each clause's scope.</remarks>
internal static class CypherPredicateTranslator
{
    private static readonly Dictionary<ExpressionType, string> Operators = new()
    {
        [ExpressionType.Equal] = "=", [ExpressionType.NotEqual] = "<>",
        [ExpressionType.LessThan] = "<", [ExpressionType.LessThanOrEqual] = "<=",
        [ExpressionType.GreaterThan] = ">", [ExpressionType.GreaterThanOrEqual] = ">="
    };
    private static readonly Dictionary<string, string> Mirrored = new()
    {
        ["="] = "=", ["<>"] = "<>", ["<"] = ">", ["<="] = ">=", [">"] = "<", [">="] = "<="
    };
    // Operator methods whose CLR meaning equals native comparison of their stored representation.
    private static readonly HashSet<Type> OperatorTypes = [typeof(string), typeof(Guid), typeof(decimal)];
    private static readonly Dictionary<string, string> TextOperators = new()
    {
        [nameof(string.StartsWith)] = "STARTS WITH", [nameof(string.EndsWith)] = "ENDS WITH", [nameof(string.Contains)] = "CONTAINS"
    };
    private static readonly HashSet<Type> TemporalTypes =
        [typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan)];

    internal static CypherPredicate Translate(Expression<Func<bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(Predicate(predicate.Body));
    }

    internal static CypherTerm OrderingKey(LambdaExpression key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } convert
            && Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type ? convert.Operand : key.Body;
        if (CypherCompiler.TryTerm(body, out var term) && IsScalar(term)) return term;
        throw CypherDiagnostics.Create("GBFC150", "An ordering key must be alias.Value.Member or a WITH value such as count.Value, and it must also be returned.");
    }

    private static CypherTerm Predicate(Expression expression)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return new CypherBooleanTerm(Predicate(and.Left), "AND", Predicate(and.Right));
            case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                return new CypherBooleanTerm(Predicate(or.Left), "OR", Predicate(or.Right));
            case BinaryExpression comparison when Operators.TryGetValue(comparison.NodeType, out var op):
                return Comparison(comparison, op);
            // The compiler accepts C# negation only over operands that can never be null in their scope.
            case UnaryExpression { NodeType: ExpressionType.Not, Method: null } not when not.Type == typeof(bool):
                return new CypherNotTerm(Predicate(not.Operand), ClrSemantics: true);
            case MethodCallExpression { Method.Name: nameof(Cypher.Is), Arguments: [var embedded] } call when call.Method.DeclaringType == typeof(Cypher):
                return Embedded(embedded);
            case MethodCallExpression call when call.Method.DeclaringType == typeof(string) && call.Object is not null:
                return Text(call);
            case MethodCallExpression call when call.Method.Name == nameof(Enumerable.Contains):
                return Membership(call);
        }
        if (expression.Type == typeof(bool) && CypherCompiler.TryTerm(expression, out var flag) && flag is CypherPropertyTerm)
            return new CypherComparisonTerm(flag, "=", new CypherLiteralTerm(true, typeof(bool)), ClrSemantics: true);
        throw CypherDiagnostics.Create("GBFC101", $"A Fluent predicate must be a comparison, a null test, a bool alias.Value member, values.Contains(member), an ordinal string StartsWith/EndsWith/Contains, Graph.Is(predicate), or a !, && or || combination of these ('{expression.NodeType}' is not translated). Use explicit predicate methods or a reviewed native query for anything else.");
    }

    private static CypherTerm Comparison(BinaryExpression comparison, string op)
    {
        var left = Operand(comparison.Left);
        var right = Operand(comparison.Right);
        if (left.Term is null && right.Term is null)
            throw CypherDiagnostics.Create("GBFC103", "A comparison needs a symbol member (alias.Value.Member or a WITH value) on at least one side.");
        if (left.Term is null)
        {
            (left, right) = (right, left);
            op = Mirrored[op];
        }
        var symbol = left.Term!;
        // A null test is two-valued in both languages, whatever operator method C# chose for it.
        if (right.Term is null && right.Value is null && op is "=" or "<>")
            return new CypherNullTerm(symbol, op == "<>");
        if (comparison.Method is null && !comparison.Left.Type.IsValueType)
            throw CypherDiagnostics.Create("GBFC105", $"'{comparison.Left.Type.Name}' {(op == "=" ? "==" : op)} compares references in C#, but native Cypher compares stored values. Compare scalar members, or use an explicit predicate method.");
        if (comparison.Method is { } method)
        {
            var declaring = Nullable.GetUnderlyingType(method.DeclaringType!) ?? method.DeclaringType!;
            if (TemporalTypes.Contains(declaring))
                throw CypherDiagnostics.Create("GBFC112", $"{declaring.Name} comparisons are not translated from C#: Fluent keeps native temporal semantics, which can differ from the CLR comparison. Use an explicit predicate such as .Property(...).Gt(value).");
            if (!OperatorTypes.Contains(declaring))
                throw CypherDiagnostics.Create("GBFC105", $"The '{method.Name}' operator defined by '{declaring.Name}' has no known native equivalent. Compare values of the property's own type.");
        }
        if (right.Term is not null)
            return new CypherComparisonTerm(symbol, op, right.Term, ClrSemantics: true);
        var value = ToMemberType(right.Value, left.MemberType!);
        // Ordering against null drops the row in both languages; it stays a native comparison.
        return new CypherComparisonTerm(symbol, op, new CypherLiteralTerm(value, left.MemberType!), ClrSemantics: true);
    }

    /// <summary>Reads the predicate passed to <c>Cypher.Is</c>. A captured predicate is read like any captured value; an
    /// inline one is built by running only Fluent Cypher builder code over captured values, never application code.</summary>
    /// <summary>Recognizes <c>Cypher.Is(predicate)</c> outside Where, for example as a returned Boolean column.</summary>
    internal static bool TryEmbedded(Expression expression, out CypherTerm term)
    {
        if (expression is MethodCallExpression { Method.Name: nameof(Cypher.Is), Arguments: [var embedded] } call && call.Method.DeclaringType == typeof(Cypher))
        {
            term = Embedded(embedded);
            return true;
        }
        term = null!;
        return false;
    }

    private static CypherTerm Embedded(Expression expression)
    {
        if (TryValue(expression, out var captured))
            return (captured as CypherPredicate ?? throw CypherDiagnostics.Create("GBFC102", "Cypher.Is(null) has no predicate to embed.")).Term;
        if (!FluentBuilder.Accepts(expression))
            throw CypherDiagnostics.Create("GBFC102", "Cypher.Is(...) accepts a captured CypherPredicate or one built inline from Fluent Cypher methods and captured symbols. Build it in a local variable first.");
        CypherPredicate? predicate;
        try { predicate = Expression.Lambda<Func<CypherPredicate>>(expression).Compile(preferInterpretation: true)(); }
        catch (Exception error) when (error is not FluentCypherException)
        {
            throw CypherDiagnostics.Create("GBFC102", $"Building the predicate passed to Cypher.Is failed: {error.Message} Build it in a local variable first.");
        }
        return (predicate ?? throw CypherDiagnostics.Create("GBFC102", "Cypher.Is(null) has no predicate to embed.")).Term;
    }

    /// <summary>Ordinal string methods only: C#'s culture-sensitive StartsWith/EndsWith(string) can disagree with native
    /// exact matching. A null member is not a match in either language (as in EF LINQ); a null argument throws in C#.</summary>
    private static CypherTerm Text(MethodCallExpression call)
    {
        var name = call.Method.Name;
        if (!TextOperators.TryGetValue(name, out var op))
            throw CypherDiagnostics.Create("GBFC101", $"string.{name} is not translated. Fluent lambdas translate the ordinal forms of StartsWith, EndsWith and Contains.");
        if (!CypherCompiler.TryTerm(call.Object!, out var term) || term is not CypherPropertyTerm)
            throw CypherDiagnostics.Create("GBFC101", $"string.{name} is translated when it is called on alias.Value.Member. For other shapes use the explicit .{name}(...) predicate method on a property expression.");
        var parameters = call.Method.GetParameters();
        var ordinal = parameters.Length == 1
            ? name == nameof(string.Contains) || parameters[0].ParameterType == typeof(char)
            : parameters.Length == 2 && parameters[1].ParameterType == typeof(StringComparison)
                && TryValue(call.Arguments[1], out var comparison) && comparison is StringComparison.Ordinal;
        if (!ordinal)
            throw CypherDiagnostics.Create("GBFC106", $"This string.{name} overload is culture-sensitive or ignores case in C#, but native {op} compares exactly. Pass StringComparison.Ordinal, or use the explicit .{name}(...) predicate method.");
        if (!TryValue(call.Arguments[0], out var value))
            throw Unread(call.Arguments[0], $"To compare with another member, use the explicit .{name}(...) predicate method.");
        var text = value switch
        {
            string s => s,
            char c => c.ToString(),
            _ => throw CypherDiagnostics.Create("GBFC102", $"string.{name}(null) throws in C#. Pass a non-null value.")
        };
        return new CypherStringTerm(term, op, new CypherLiteralTerm(text, typeof(string)));
    }

    /// <summary>Translates <c>values.Contains(member)</c> to <c>IN</c>. Only collections whose Contains is default
    /// equality and which can be read without running application code are accepted; the values are copied now.</summary>
    private static CypherTerm Membership(MethodCallExpression call)
    {
        Expression source, item;
        if (call.Object is not null && call.Arguments.Count == 1)
            (source, item) = (call.Object, call.Arguments[0]);
        else if (call.Object is null && call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
            (source, item) = (call.Arguments[0], call.Arguments[1]);
        // C# 14 binds array.Contains(x) to MemoryExtensions.Contains(ReadOnlySpan<T>, T, comparer = null).
        else if (call.Object is null && call.Method.DeclaringType == typeof(MemoryExtensions)
                 && (call.Arguments.Count == 2 || call.Arguments is [_, _, ConstantExpression { Value: null }]))
            (source, item) = (Unspan(call.Arguments[0]), call.Arguments[1]);
        else
            throw CypherDiagnostics.Create("GBFC107", "This Contains overload is not translated: a custom equality comparer has no native equivalent. Call Contains(value) on an array, List<T> or HashSet<T>.");
        if (CypherCompiler.TryTerm(source, out _))
            throw CypherDiagnostics.Create("GBFC101", "Membership in a list stored on a node or edge is not translated. Use a reviewed native query.");
        var side = Operand(item);
        if (side.Term is null)
            throw CypherDiagnostics.Create("GBFC103", "values.Contains(...) needs alias.Value.Member or a WITH value as its argument.");
        var memberType = side.MemberType!;
        if (TemporalTypes.Contains(memberType))
            throw CypherDiagnostics.Create("GBFC112", $"{memberType.Name} membership is not translated from C#: Fluent keeps native temporal semantics, which can differ from CLR equality. Use an explicit predicate such as .Property(...).In(values).");
        if (!TryValue(source, out var collection))
            throw Unread(source, "");
        var values = new List<object?>();
        foreach (var element in Elements(collection))
        {
            if (element is null) values.Add(null);
            else if (element is double.NaN or float.NaN)
                throw CypherDiagnostics.Create("GBFC105", "NaN is equal to itself in Contains but not in native IN. Remove NaN from the values.");
            // A value the member's type cannot represent can never be equal to it, in C# or natively.
            else if (TryMemberType(element, memberType, out var converted)) values.Add(converted);
        }
        // Null elements are resolved by the compiler, which knows whether the member can be null in its scope.
        return new CypherInTerm(side.Term, values, ClrSemantics: true);
    }

    private static Expression Unspan(Expression expression) => expression switch
    {
        MethodCallExpression { Method.Name: "op_Implicit", Arguments: [var array] } => array,
        UnaryExpression { NodeType: ExpressionType.Convert, Method.Name: "op_Implicit" } convert => convert.Operand,
        _ => expression
    };

    private static IEnumerable<object?> Elements(object? collection)
    {
        var type = collection?.GetType();
        var definition = type is { IsGenericType: true } ? type.GetGenericTypeDefinition() : null;
        if (collection is Array { Rank: 1 } || definition == typeof(List<>))
            return ((IEnumerable)collection!).Cast<object?>();
        if (definition == typeof(HashSet<>))
        {
            var comparer = type!.GetProperty(nameof(HashSet<int>.Comparer))!.GetValue(collection);
            var standard = typeof(EqualityComparer<>).MakeGenericType(type.GetGenericArguments()[0])
                .GetProperty(nameof(EqualityComparer<int>.Default))!.GetValue(null);
            if (ReferenceEquals(comparer, standard) || ReferenceEquals(comparer, StringComparer.Ordinal))
                return ((IEnumerable)collection!).Cast<object?>();
            throw CypherDiagnostics.Create("GBFC107", "This HashSet uses a custom equality comparer, which has no native equivalent. Use a set with default equality, or a reviewed native query.");
        }
        throw collection is null
            ? CypherDiagnostics.Create("GBFC102", "The collection is null; C# Contains would throw.")
            : CypherDiagnostics.Create("GBFC107", $"Contains is translated over arrays, List<T> and HashSet<T>; reading '{type!.Name}' could run application code. Copy the values into an array or List<T> first.");
    }

    private static Side Operand(Expression expression)
    {
        var candidate = expression;
        while (candidate is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: null } convert
               && Widens(convert.Operand.Type, convert.Type))
            candidate = convert.Operand;
        if (CypherCompiler.TryTerm(candidate, out var term))
        {
            if (!IsScalar(term))
                throw CypherDiagnostics.Create("GBFC103", "Compare scalar members (alias.Value.Member) or WITH values, not node, edge or path symbols.");
            return new(term, Nullable.GetUnderlyingType(candidate.Type) ?? candidate.Type, null);
        }
        if (TryValue(expression, out var value))
        {
            if (value is CypherAlias)
                throw CypherDiagnostics.Create("GBFC103", "Compare scalar members (alias.Value.Member) or WITH values, not symbols themselves.");
            return new(null, null, value);
        }
        throw Unread(expression, "");
    }

    private static NotSupportedException Unread(Expression expression, string alternative)
    {
        var finder = new SymbolFinder();
        finder.Visit(expression);
        return finder.Found
            ? CypherDiagnostics.Create("GBFC101", $"'{expression.NodeType}' over a symbol value is not translated. Compare alias.Value.Member directly; method calls, arithmetic and client computation never run in a Fluent query. {alternative}".TrimEnd())
            : CypherDiagnostics.Create("GBFC102", $"The compared value ('{expression.NodeType}') is not read during translation because that could run application code. Assign it to a local variable first.");
    }

    /// <summary>Reads constants, closure fields and static fields only; never property getters or methods.</summary>
    private static bool TryValue(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;
            case MemberExpression { Member: FieldInfo field, Expression: null }:
                try { value = field.GetValue(null); }
                catch (Exception error) when (error is TypeInitializationException or TargetInvocationException { InnerException: TypeInitializationException })
                {
                    throw CypherDiagnostics.Create("GBFC102", $"Reading static field '{field.Name}' ran its type's initializer, which failed: {error.GetBaseException().Message}");
                }
                return true;
            case MemberExpression { Member: FieldInfo field, Expression: { } owner }
                when TryValue(owner, out var receiver) && receiver is not null && field.DeclaringType!.IsInstanceOfType(receiver):
                value = field.GetValue(receiver);
                return true;
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: null } convert
                when TryValue(convert.Operand, out var operand):
                value = ConvertValue(operand, convert.Type);
                return true;
        }
        value = null;
        return false;
    }

    private static object? ConvertValue(object? value, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (value is null)
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null ? null
                : throw CypherDiagnostics.Create("GBFC102", "A null captured value is converted to a non-nullable type; C# would throw here.");
        if (target.IsInstanceOfType(value)) return value;
        if (Widens(value.GetType(), target))
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        throw CypherDiagnostics.Create("GBFC105", $"Converting the compared value from '{value.GetType().Name}' to '{target.Name}' is not a lossless conversion. Compare a value of the property's own type.");
    }

    /// <summary>Returns the compared value as the member's own CLR type, so EF converters receive model values.</summary>
    private static object? ToMemberType(object? value, Type memberType)
        => TryMemberType(value, memberType, out var converted) ? converted
            : throw CypherDiagnostics.Create("GBFC105", $"The compared value cannot be represented exactly as '{memberType.Name}', the property's type. Compare a value of the property's own type.");

    private static bool TryMemberType(object? value, Type memberType, out object? converted)
    {
        converted = value;
        if (value is null || memberType.IsInstanceOfType(value)) return true;
        try
        {
            if (memberType.IsEnum && IsIntegral(value.GetType()))
            {
                // Enum.ToObject truncates; a checked conversion to the underlying type rejects out-of-range values.
                var underlying = Convert.ChangeType(value, Enum.GetUnderlyingType(memberType), CultureInfo.InvariantCulture);
                converted = Enum.ToObject(memberType, underlying);
                return true;
            }
            if (IsNumeric(memberType) && IsNumeric(value.GetType()))
            {
                // Exact only if converting back in the value's own type returns the same value (decimal would round doubles).
                converted = Convert.ChangeType(value, memberType, CultureInfo.InvariantCulture);
                return Convert.ChangeType(converted, value.GetType(), CultureInfo.InvariantCulture).Equals(value);
            }
        }
        catch (OverflowException) { }
        return false;
    }

    /// <summary>Conversions that cannot change which values compare equal or ordered: nullable wrapping, enum to
    /// its underlying type (and on through widening), integral widening and small integral or float values to double.</summary>
    private static bool Widens(Type from, Type to)
    {
        if (Nullable.GetUnderlyingType(from) is not null && Nullable.GetUnderlyingType(to) is null) return false;
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (source == target) return true;
        if (source.IsEnum) source = Enum.GetUnderlyingType(source);
        if (source == target) return true;
        var (sourceRank, targetRank) = (Rank(source), Rank(target));
        if (sourceRank > 0 && targetRank > 0)
            return targetRank > sourceRank && (IsSigned(target) || !IsSigned(source));
        return (sourceRank is > 0 and < 4 || source == typeof(float)) && target == typeof(double);
    }

    private static int Rank(Type type)
        => type == typeof(sbyte) || type == typeof(byte) ? 1
            : type == typeof(short) || type == typeof(ushort) ? 2
            : type == typeof(int) || type == typeof(uint) ? 3
            : type == typeof(long) ? 4 : 0;
    private static bool IsSigned(Type type) => type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long);
    private static bool IsIntegral(Type type) => Rank(type) > 0 || type == typeof(ulong);
    private static bool IsNumeric(Type type) => IsIntegral(type) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    private static bool IsScalar(CypherTerm term) => term is CypherPropertyTerm or CypherAliasTerm { Alias.Kind: CypherAliasKind.Value };

    private readonly record struct Side(CypherTerm? Term, Type? MemberType, object? Value);

    /// <summary>Accepts expressions that only call Fluent Cypher members and read constants or fields. Quoted lambdas
    /// (such as Property(m => m.Title)) are inspected by the builder, never invoked, so their bodies are not visited.</summary>
    private sealed class FluentBuilder : ExpressionVisitor
    {
        private bool _rejected;
        internal static bool Accepts(Expression expression)
        {
            var visitor = new FluentBuilder();
            visitor.Visit(expression);
            return !visitor._rejected;
        }
        private static bool Fluent(MemberInfo member) => member.DeclaringType is { } type
            && type.Namespace == typeof(Cypher).Namespace && type.Assembly == typeof(Cypher).Assembly;
        public override Expression? Visit(Expression? node)
        {
            if (node is null || _rejected) return node;
            switch (node)
            {
                case ConstantExpression or UnaryExpression { NodeType: ExpressionType.Quote }:
                    return node;
                case MemberExpression { Member: FieldInfo }:
                // Symbol .Value getters exist only for lambdas and throw when called.
                case MemberExpression { Member: PropertyInfo property } when Fluent(property) && property.Name != "Value":
                case MethodCallExpression call when Fluent(call.Method):
                case NewArrayExpression { NodeType: ExpressionType.NewArrayInit }:
                case UnaryExpression { NodeType: ExpressionType.Convert, Method: null }:
                    return base.Visit(node);
                default:
                    _rejected = true;
                    return node;
            }
        }
    }

    private sealed class SymbolFinder : ExpressionVisitor
    {
        internal bool Found { get; private set; }
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.Name == "Value" && CypherCompiler.ReadCapture(node.Expression) is CypherAlias) Found = true;
            return base.VisitMember(node);
        }
    }
}
