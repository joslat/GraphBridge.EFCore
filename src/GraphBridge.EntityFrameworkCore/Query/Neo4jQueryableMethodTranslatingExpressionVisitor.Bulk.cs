using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    protected override Expression TranslateExecuteDelete(ShapedQueryExpression source) => TranslateBulk(source, null);

    protected override Expression TranslateExecuteUpdate(ShapedQueryExpression source, IReadOnlyList<ExecuteUpdateSetter> setters)
        => TranslateBulk(source, setters);

    private Neo4jBulkExpression TranslateBulk(ShapedQueryExpression source, IReadOnlyList<ExecuteUpdateSetter>? setters)
    {
        if (!QueryCompilationContext.IsAsync) throw Unsupported("Bulk mutations require ExecuteUpdateAsync or ExecuteDeleteAsync");
        RequireEntityShape(source);
        var query = GetQuery(source);
        if (query.Include is not null || query.AggregateType is not null)
            throw Unsupported("Bulk mutations require a mapped entity root without Include or aggregation");
        var entityType = query.Binding.EntityType;
        Neo4jBulkTarget targetBinding;
        switch (bindings.GetStorageKind(entityType))
        {
            case Metadata.GraphStorageKind.Node:
                targetBinding = new(bindings.GetNode(entityType), bindings.GetNodeReferences(entityType).SingleOrDefault());
                break;
            case Metadata.GraphStorageKind.AssociationEdge:
                var association = bindings.GetAssociation(entityType);
                targetBinding = new(Source: association.Source, Target: association.Target,
                    RelationshipLock: association.PayloadProperties.FirstOrDefault(p => !p.Property.IsNullable)
                        ?? association.PayloadProperties.FirstOrDefault());
                break;
            case Metadata.GraphStorageKind.IdentifiedEdge:
                var edge = bindings.GetIdentifiedEdge(entityType);
                targetBinding = new(Source: edge.Source, Target: edge.Target, RelationshipLock: edge.KeyProperty);
                break;
            default: throw Unsupported("Unknown bulk graph storage kind");
        }
        foreach (var property in query.Binding.Properties.Select(p => p.ModelProperty))
            if (property.Property.ValueGenerated != ValueGenerated.Never && !property.Property.IsPrimaryKey()
                && !(property.Property.IsConcurrencyToken && property.Property.ValueGenerated == ValueGenerated.OnAddOrUpdate
                    && property.Property.ClrType == typeof(long) && property.Property.GetTypeMapping().Converter is null))
                throw Unsupported("Bulk mutations require client-valued properties or an unconverted long row version");
        if (setters is null) return new Neo4jBulkExpression(query, targetBinding, null);
        if (setters.Count is 0 or > 64) throw Unsupported("Bulk update requires between 1 and 64 distinct scalar setters");
        var parameters = query.Parameters.ToList();
        var assignments = new List<Neo4jBulkAssignment>();
        foreach (var setter in setters)
        {
            var target = FindProperty(setter.PropertySelector.Body, setter.PropertySelector.Parameters[0], query.Binding)
                ?? throw Unsupported("Bulk setters require a direct mapped scalar property");
            var metadata = target.ModelProperty.Property;
            if (metadata.IsKey() || metadata.IsForeignKey() || metadata.ValueGenerated != ValueGenerated.Never)
                throw Unsupported("a bulk setter that changes a key, an endpoint or a generated property (row versions advance automatically)");
            if (assignments.Any(a => a.Target == target)) throw Unsupported("Duplicate bulk property setters");
            var value = setter.ValueExpression;
            if (value is LambdaExpression lambda)
            {
                value = StripIdentityConversion(lambda.Body);
                if (FindProperty(value, lambda.Parameters[0], query.Binding) is { } copy)
                {
                    if (copy.ModelProperty.Property.ClrType != metadata.ClrType)
                        throw Unsupported("Bulk property copies require identical model scalar types");
                    assignments.Add(new(target, Neo4jBulkValueKind.Copy, copy));
                    continue;
                }
                if (value is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.AddChecked
                    or ExpressionType.Subtract or ExpressionType.SubtractChecked } binary
                    && FindProperty(binary.Left, lambda.Parameters[0], query.Binding) is { } operand)
                {
                    var scalar = Nullable.GetUnderlyingType(metadata.ClrType) ?? metadata.ClrType;
                    if (scalar != typeof(int) && scalar != typeof(long) || metadata.ClrType != operand.ModelProperty.Property.ClrType
                        || HasExplicitConversion(metadata) || HasExplicitConversion(operand.ModelProperty.Property))
                        throw Unsupported("Bulk arithmetic requires matching unconverted int/long properties");
                    var parameter = AddParameter(binary.Right, operand.ModelProperty, parameters);
                    assignments.Add(new(target, binary.NodeType is ExpressionType.Add or ExpressionType.AddChecked
                        ? Neo4jBulkValueKind.Add : Neo4jBulkValueKind.Subtract, operand, parameter.Name));
                    continue;
                }
            }
            assignments.Add(new(target, Neo4jBulkValueKind.Constant, Parameter: AddParameter(value, target.ModelProperty, parameters).Name));
        }
        return new Neo4jBulkExpression(query.With(query.Plan, parameters), targetBinding, assignments);
    }
}
