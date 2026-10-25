using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed class Neo4jQueryableMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    GraphBindingCompiler bindings) : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new Neo4jQueryableMethodTranslatingExpressionVisitor(dependencies, queryCompilationContext, bindings);
}

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext,
    GraphBindingCompiler bindings)
    : QueryableMethodTranslatingExpressionVisitor(dependencies, queryCompilationContext, subquery: false)
{
    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => throw Unsupported("Subqueries");

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is not Neo4jIncludeExpression include)
            return base.VisitExtension(extensionExpression);
        if (Visit(include.Source) is not ShapedQueryExpression shaped)
            throw Unsupported("Include without an entity query");
        RequireEntityShape(shaped);
        var query = GetQuery(shaped);
        if (query.AggregateType is not null || query.Include is not null)
            throw Unsupported("aggregated or multiple Include");
        var binding = GraphIncludeBinding.Create(query.Binding, include.Navigation, bindings);
        return shaped.UpdateQueryExpression(query.With(query.Plan, include: binding));
    }

    protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
    {
        var binding = bindings.GetStorageKind(entityType) switch
        {
            GraphStorageKind.Node => bindings.GetNodeReferences(entityType).SingleOrDefault() is { } reference
                ? GraphReadBinding.FromNodeReference(reference) : GraphReadBinding.FromNode(bindings.GetNode(entityType)),
            GraphStorageKind.AssociationEdge => GraphReadBinding.FromAssociation(bindings.GetAssociation(entityType)),
            GraphStorageKind.IdentifiedEdge => GraphReadBinding.FromIdentifiedEdge(bindings.GetIdentifiedEdge(entityType)),
            _ => throw Unsupported("Unknown graph storage kind")
        };
        var query = new Neo4jQueryExpression(binding, binding.CreatePlan());
        return new ShapedQueryExpression(query,
            new StructuralTypeShaperExpression(entityType, Neo4jQueryExpression.RowParameter, nullable: false));
    }

    protected override ShapedQueryExpression TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
    {
        var query = GetQuery(source);
        RequireEntityShape(source);
        RequireBeforePagination(query, "A filter (Where, or a predicate passed to Any, First, Single or Count)");
        var parameters = query.Parameters.ToList();
        var translated = TranslatePredicate(predicate.Body, predicate.Parameters[0], query.Binding, parameters);
        var combined = query.Plan.Predicate is null ? translated
            : new CypherBinary(query.Plan.Predicate, CypherOperator.And, translated);
        return source.UpdateQueryExpression(query.With(query.Plan with { Predicate = combined }, parameters));
    }

    private static CypherExpression TranslatePredicate(
        Expression expression, ParameterExpression entity, GraphReadBinding binding, List<Neo4jQueryParameter> parameters)
    {
        expression = StripIdentityConversion(expression);
        if (expression is UnaryExpression { NodeType: ExpressionType.Not } unary)
            return new CypherNot(TranslatePredicate(unary.Operand, entity, binding, parameters));
        if (expression is MethodCallExpression contains && IsContainsMethod(contains))
            return TranslateCollectionContains(contains, entity, binding, parameters);
        if (expression is MethodCallExpression equals
            && equals.Method == typeof(object).GetMethod(nameof(object.Equals), [typeof(object), typeof(object)]))
            return TranslateObjectEquals(equals, entity, binding, parameters);
        if (expression is MethodCallExpression instanceEquals && IsInstanceEquals(instanceEquals))
            return TranslateInstanceEquals(instanceEquals, entity, binding, parameters);
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
                return new CypherBinary(
                    TranslatePredicate(binary.Left, entity, binding, parameters),
                    binary.NodeType == ExpressionType.AndAlso ? CypherOperator.And : CypherOperator.Or,
                    TranslatePredicate(binary.Right, entity, binding, parameters));

            var operation = binary.NodeType switch
            {
                ExpressionType.Equal => CypherOperator.Equal,
                ExpressionType.NotEqual => CypherOperator.NotEqual,
                ExpressionType.GreaterThan => CypherOperator.GreaterThan,
                ExpressionType.GreaterThanOrEqual => CypherOperator.GreaterThanOrEqual,
                ExpressionType.LessThan => CypherOperator.LessThan,
                ExpressionType.LessThanOrEqual => CypherOperator.LessThanOrEqual,
                _ => throw Unsupported($"Predicate operator {binary.NodeType}")
            };
            var leftProperty = FindProperty(binary.Left, entity, binding);
            var rightProperty = FindProperty(binary.Right, entity, binding);
            // A vector can only be tested for null; similarity belongs to typed vector search (ADR 0054).
            if ((IsVector(leftProperty) || IsVector(rightProperty)) && !(operation is CypherOperator.Equal or CypherOperator.NotEqual
                    && (leftProperty is null && IsNullConstant(binary.Left) || rightProperty is null && IsNullConstant(binary.Right))))
                throw RefuseVector("Predicate on a vector property other than a null test");
            if (operation is not (CypherOperator.Equal or CypherOperator.NotEqual)
                && (HasExplicitConversion(leftProperty?.ModelProperty.Property) || HasExplicitConversion(rightProperty?.ModelProperty.Property)))
                throw Unsupported("Range comparisons on properties with explicit converters");
            if (leftProperty is not null && rightProperty is not null
                && leftProperty.ModelProperty.UsesNativeInstantSemantics != rightProperty.ModelProperty.UsesNativeInstantSemantics)
                throw Unsupported("Comparisons between native and converted temporal properties");
            if (leftProperty is not null && rightProperty is not null
                && (HasExplicitConversion(leftProperty.ModelProperty.Property) || HasExplicitConversion(rightProperty.ModelProperty.Property))
                && !ReferenceEquals(leftProperty.ModelProperty.Property.GetTypeMapping().Converter, rightProperty.ModelProperty.Property.GetTypeMapping().Converter))
                throw Unsupported("Comparisons between properties with different explicit converters");
            return new CypherBinary(
                TranslateOperand(binary.Left, leftProperty, rightProperty, parameters), operation,
                TranslateOperand(binary.Right, rightProperty, leftProperty, parameters));
        }
        var property = FindProperty(expression, entity, binding);
        if (expression.Type == typeof(bool))
            return property is null ? AddParameter(expression, null, parameters)
                : new CypherBinary(property.StoreProperty, CypherOperator.Equal,
                    AddParameter(Expression.Constant(true), property.ModelProperty, parameters));
        throw Unsupported($"Predicate expression {expression.NodeType}");
    }

    private static CypherExpression TranslateOperand(Expression expression, GraphReadProperty? property,
        GraphReadProperty? otherProperty, List<Neo4jQueryParameter> parameters)
    {
        CypherExpression operand = property is null ? AddParameter(expression, otherProperty?.ModelProperty, parameters) : property.StoreProperty;
        return (property ?? otherProperty)?.ModelProperty.UsesNativeInstantSemantics == true
            ? new CypherUtcInstant(operand) : operand;
    }

    private static CypherParameter AddParameter(Expression expression, GraphPropertyBinding? property,
        List<Neo4jQueryParameter> parameters, bool nonNegative = false)
    {
        expression = StripIdentityConversion(expression);
        // EF retains a literal Guid(string) constructor as NewExpression. Fold only this
        // known scalar form; never compile/evaluate arbitrary constructors or client code.
        if (expression is NewExpression { Arguments: [ConstantExpression { Value: string literal }] } creation
            && creation.Constructor == typeof(Guid).GetConstructor([typeof(string)]))
        {
            if (!Guid.TryParse(literal, out var guid))
                throw Unsupported("Guid construction requires a valid constant string");
            expression = Expression.Constant(guid);
        }
        var name = "p" + parameters.Count;
        parameters.Add(expression switch
        {
            QueryParameterExpression parameter => new(name, parameter.Name, null, property, nonNegative),
            ConstantExpression constant => new(name, null, constant.Value, property, nonNegative),
            MethodCallExpression call => throw Unsupported($"the method {call.Method.DeclaringType?.Name}.{call.Method.Name} as a query value"),
            _ => throw Unsupported($"Value expression {expression.NodeType}; only EF parameters and constants are supported")
        });
        return new CypherParameter(name);
    }

    private static GraphReadProperty? FindProperty(Expression expression, ParameterExpression entity, GraphReadBinding binding)
    {
        expression = StripIdentityConversion(expression);
        string? name = expression switch
        {
            MemberExpression member when member.Expression is not null
                && StripIdentityConversion(member.Expression) == entity => member.Member.Name,
            MethodCallExpression method when method.Method.DeclaringType == typeof(EF)
                && method.Method.Name == nameof(EF.Property)
                && StripIdentityConversion(method.Arguments[0]) == entity
                && method.Arguments[1] is ConstantExpression { Value: string propertyName } => propertyName,
            _ => null
        };
        return name is null ? null : binding.Properties.SingleOrDefault(p => p.ModelProperty.Property.Name == name)
            ?? throw Unsupported($"Unmapped property {name}");
    }

    private static Expression StripIdentityConversion(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
               && IsRepresentationConversion(convert))
            expression = convert.Operand;
        return expression;
    }

    private static bool IsRepresentationConversion(UnaryExpression convert)
    {
        var source = Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type;
        var target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;
        // C# expresses enum equality using its underlying integer. Its model converter
        // still needs the enum value, so preserve that model value for parameter binding.
        return source == target
            || (source.IsEnum && Enum.GetUnderlyingType(source) == target)
            || (target == typeof(object) && convert.Operand is ParameterExpression && !source.IsValueType);
    }

    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
    {
        if (selector.Body == selector.Parameters[0])
            return source;
        RequireEntityShape(source);
        var query = GetQuery(source);
        if (query.Include is not null)
            throw Unsupported("Non-identity projections combined with Include");
        var translator = new ProjectionTranslator(selector.Parameters[0], query.Binding, source.ShaperExpression);
        var projection = translator.Visit(selector.Body)!;
        var properties = translator.Properties.OrderBy(p => p.Index).ToArray();
        return source.Update(query.With(query.Plan with
        {
            Columns = query.Binding.CreatePlan(properties).Columns
        }, resultProperties: properties), projection);
    }

    protected override ShapedQueryExpression TranslateOrderBy(ShapedQueryExpression source, LambdaExpression keySelector, bool ascending)
        => ApplyOrdering(source, keySelector, ascending, append: false);

    protected override ShapedQueryExpression TranslateThenBy(ShapedQueryExpression source, LambdaExpression keySelector, bool ascending)
        => ApplyOrdering(source, keySelector, ascending, append: true);

    private static ShapedQueryExpression ApplyOrdering(ShapedQueryExpression source, LambdaExpression selector, bool ascending, bool append)
    {
        RequireEntityShape(source);
        var query = GetQuery(source);
        RequireBeforePagination(query, "Ordering (OrderBy or ThenBy)");
        var property = FindProperty(selector.Body, selector.Parameters[0], query.Binding)
            ?? throw Unsupported("Ordering requires a direct scalar property");
        if (IsVector(property))
            throw RefuseVector("Ordering by a vector property");
        if (HasExplicitConversion(property.ModelProperty.Property))
            throw Unsupported("Ordering properties with explicit converters");
        var orderings = append ? query.Plan.Orderings?.ToList() ?? [] : new List<CypherOrdering>();
        orderings.Add(new CypherOrdering(property.StoreProperty.Name, ascending, property.StoreProperty.Owner,
            property.ModelProperty.UsesNativeInstantSemantics));
        return source.UpdateQueryExpression(query.With(query.Plan with { Orderings = orderings }));
    }

    protected override ShapedQueryExpression TranslateSkip(ShapedQueryExpression source, Expression count)
    {
        var query = GetQuery(source);
        RequireBeforePagination(query, "Skip");
        var parameters = query.Parameters.ToList();
        var parameter = AddParameter(count, null, parameters, nonNegative: true);
        return source.UpdateQueryExpression(query.With(query.Plan with { SkipParameter = parameter.Name }, parameters));
    }

    protected override ShapedQueryExpression TranslateTake(ShapedQueryExpression source, Expression count)
    {
        var query = GetQuery(source);
        if (query.Plan.LimitParameter is not null)
            throw Unsupported("Repeated Take");
        var parameters = query.Parameters.ToList();
        var parameter = AddParameter(count, null, parameters, nonNegative: true);
        return source.UpdateQueryExpression(query.With(query.Plan with { LimitParameter = parameter.Name }, parameters));
    }

    protected override ShapedQueryExpression TranslateFirstOrDefault(
        ShapedQueryExpression source, LambdaExpression? predicate, Type returnType, bool returnDefault)
        => ApplyTerminalLimit(source, predicate, 1);

    protected override ShapedQueryExpression TranslateSingleOrDefault(
        ShapedQueryExpression source, LambdaExpression? predicate, Type returnType, bool returnDefault)
        => ApplyTerminalLimit(source, predicate, 2);

    private ShapedQueryExpression ApplyTerminalLimit(ShapedQueryExpression source, LambdaExpression? predicate, int maximum)
    {
        if (predicate is not null)
            source = TranslateWhere(source, predicate);
        var query = GetQuery(source);
        var parameters = query.Parameters.ToList();
        if (query.Plan.LimitParameter is { } name)
        {
            var index = parameters.FindIndex(p => p.Name == name);
            parameters[index] = parameters[index] with { Maximum = maximum };
        }
        else
        {
            name = AddParameter(Expression.Constant(maximum), null, parameters, nonNegative: true).Name;
        }
        return source.UpdateQueryExpression(query.With(query.Plan with { LimitParameter = name }, parameters));
    }

    protected override ShapedQueryExpression TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => ApplyAggregate(source, predicate, CypherAggregate.Count, typeof(int));
    protected override ShapedQueryExpression TranslateLongCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => ApplyAggregate(source, predicate, CypherAggregate.Count, typeof(long));
    protected override ShapedQueryExpression TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate)
        => ApplyAggregate(source, predicate, CypherAggregate.Any, typeof(bool));

    private ShapedQueryExpression ApplyAggregate(ShapedQueryExpression source, LambdaExpression? predicate,
        CypherAggregate aggregate, Type resultType)
    {
        if (GetQuery(source).Binding.Root is CypherAssociationRoot)
            throw Unsupported("Association aggregates require preservation of per-row physical identity diagnostics");
        if (GetQuery(source).Binding.Root is CypherIdentifiedEdgeRoot)
            throw Unsupported("Identified-edge aggregates require preservation of per-row physical identity diagnostics");
        if (GetQuery(source).Binding.Root is CypherNodeReferenceRoot)
            throw Unsupported("Native-reference aggregates require preservation of per-row membership diagnostics");
        if (predicate is not null)
            source = TranslateWhere(source, predicate);
        var query = GetQuery(source);
        RequireBeforePagination(query, "An aggregate");
        var shaper = Expression.Convert(
            Expression.MakeIndex(Neo4jQueryExpression.RowParameter, typeof(ValueBuffer).GetProperty("Item")!, [Expression.Constant(0)]),
            resultType);
        return source.Update(query.With(query.Plan with { Aggregate = aggregate, Orderings = null }, aggregateType: resultType), shaper);
    }

    private static Neo4jQueryExpression GetQuery(ShapedQueryExpression source)
        => source.QueryExpression as Neo4jQueryExpression ?? throw Unsupported("Unmapped query roots");

    private static bool HasExplicitConversion(IProperty? property)
    {
        if (property is null)
            return false;
        if (property.GetValueConverter() is not null || property.GetProviderClrType() is not null)
            return true;
        // Relationship FKs can inherit a converter from the principal key. Its ordering semantics
        // are no safer than an explicitly converted payload; built-in scalar converters remain allowed.
        foreach (var foreignKey in property.GetContainingForeignKeys())
        {
            var index = foreignKey.Properties.ToList().IndexOf(property);
            var principal = foreignKey.PrincipalKey.Properties[index];
            if (principal != property && HasExplicitConversion(principal))
                return true;
        }
        return false;
    }

    private static void RequireEntityShape(ShapedQueryExpression source)
    {
        if (source.ShaperExpression is not StructuralTypeShaperExpression)
            throw Unsupported("This operator after scalar projection");
    }

    private static void RequireBeforePagination(Neo4jQueryExpression query, string operation)
    {
        if (query.Plan.SkipParameter is not null || query.Plan.LimitParameter is not null)
            throw Unsupported(operation + " after Skip or Take");
    }

    private static Neo4jQueryTranslationException Unsupported(string feature) => Neo4jQueryRefusals.Create(feature);

    private static bool IsVector(GraphReadProperty? property) => property?.ModelProperty.VectorDimensions is not null;
    private static bool IsNullConstant(Expression expression) => StripIdentityConversion(expression) is ConstantExpression { Value: null };
    private static Neo4jQueryTranslationException RefuseVector(string feature) => Neo4jQueryRefusals.Create(feature,
        "Find similar entities with typed vector search (Database.QueryVectorNodesAsync) and load them by key. LINQ returns a vector property and tests it for null.");
}
