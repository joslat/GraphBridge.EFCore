using System.Collections;
using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

/// <summary>One native-reference Include, shaped by EF with one complete included collection per root.</summary>
internal sealed class GraphIncludeBinding
{
    private readonly Action<object, object> _fixup;
    private GraphIncludeBinding(INavigation navigation, NodeReferenceBinding reference, GraphReadBinding included)
    {
        Navigation = navigation;
        Reference = reference;
        Included = included;
        _fixup = CompileFixup(navigation);
        RequiredUniqueEndpoints = Array.AsReadOnly(included.RequiredUniqueEndpoints
            .Concat(new[] { reference.Principal, reference.Dependent }).Distinct().ToArray());
    }

    public INavigation Navigation { get; }
    public NodeReferenceBinding Reference { get; }
    public GraphReadBinding Included { get; }
    public IReadOnlyList<NodeBinding> RequiredUniqueEndpoints { get; }
    public CypherIncludePlan CreatePlan(CypherQueryPlan root)
        => new(root, Included.CreatePlan(), Navigation.IsCollection, Reference.RelationshipType,
            Navigation.IsCollection ? Reference.Dependent.Labels : Reference.Principal.Labels);

    public static GraphIncludeBinding Create(GraphReadBinding root, INavigation navigation, GraphBindingCompiler compiler)
    {
        if (navigation.DeclaringEntityType != root.EntityType
            || root.Root is not (CypherNodeRoot or CypherNodeReferenceRoot)
            || navigation.ForeignKey.FindAnnotation(GraphAnnotationNames.NodeReferenceType)?.Value is not string)
            throw Unsupported("Include requires a direct mapped native-reference navigation on the root entity");
        var target = navigation.TargetEntityType;
        if (target.GetDeclaredQueryFilters().Count != 0
            || target.GetNavigations().Any(n => n.IsEagerLoaded)
            || target.GetSkipNavigations().Any(n => n.IsEagerLoaded))
            throw Unsupported("Include targets with query filters or AutoInclude require additional translation support");
        var reference = compiler.GetNodeReference(navigation.ForeignKey);
        if (!navigation.IsCollection && compiler.GetNodeReferences(target).Count != 0)
            throw Unsupported("a reference Include whose target also has its own native parent, because its membership cannot be verified yet");
        var included = navigation.IsCollection ? GraphReadBinding.FromNodeReference(reference)
            : GraphReadBinding.FromNode(reference.Principal);
        return new GraphIncludeBinding(navigation, reference, included);
    }

    /// <summary>Decode every related row before either root or related EF materializer is invoked.</summary>
    public IReadOnlyList<ValueBuffer> ReadRows(object? raw, Action checkCancellation)
    {
        if (raw is not IEnumerable rows || raw is string or IDictionary)
            throw new InvalidOperationException("GraphBridge Include requires a collection of related value maps.");
        var values = new List<ValueBuffer>();
        foreach (var item in rows)
        {
            checkCancellation();
            if (item is not IReadOnlyDictionary<string, object?> row
                || !row.TryGetValue(CypherIncludePlan.IncludedLabelAlias, out var label) || label is not true)
                throw new InvalidOperationException("GraphBridge Include related node label diagnostic is missing or invalid.");
            values.Add(Included.ReadRow(row));
        }
        if (!Navigation.IsCollection && (values.Count > 1 || Reference.ForeignKey.IsRequired && values.Count != 1))
            throw new InvalidOperationException("GraphBridge reference Include must return exactly one related value map for a required reference, or zero or one for an optional reference.");
        return values;
    }

    public void InitializeCollection(object entity)
    {
        if (Navigation.IsCollection && !Navigation.IsShadowProperty())
            Navigation.GetCollectionAccessor()!.GetOrCreate(entity, forMaterialization: true);
    }

    public void Fixup(QueryContext context, object entity, object related, QueryTrackingBehavior tracking)
    {
        // EF's tracked materializers already perform fixup, preserving pretracked local changes.
        // The no-tracking modes follow the pinned EF InMemory provider's metadata accessor pattern.
        if (tracking != QueryTrackingBehavior.TrackAll)
            _fixup(entity, related);
        if (tracking != QueryTrackingBehavior.TrackAll && Navigation.Inverse is { IsCollection: false } inverse)
        {
            if (tracking != QueryTrackingBehavior.NoTracking)
                context.SetNavigationIsLoaded(related, inverse);
            ((INavigationBase)inverse).SetIsLoadedWhenNoTracking(related);
        }
    }

    public void MarkLoaded(QueryContext context, object entity, QueryTrackingBehavior tracking, bool noRelatedRows = false)
    {
        // A non-null tracked reference is marked by EF only if it actually fixes up. The current
        // FK may have been changed locally to another, untracked parent; do not falsely mark it loaded.
        if (tracking == QueryTrackingBehavior.NoTrackingWithIdentityResolution
            || (tracking == QueryTrackingBehavior.TrackAll && (Navigation.IsCollection
                || noRelatedRows && !Reference.ForeignKey.IsRequired
                    && CurrentTrackedForeignKeyIsNull(context, entity))))
            context.SetNavigationIsLoaded(entity, Navigation);
        if (tracking != QueryTrackingBehavior.TrackAll)
            ((INavigationBase)Navigation).SetIsLoadedWhenNoTracking(entity);
    }

    // EF 10.0.12 QueryContext uses this scoped manager for TrackAll. Read current
    // values without DbContext.Entry's automatic DetectChanges or a second tracker.
    // Audited IStateManager.TryGetEntry + InternalEntryBase indexer: EF_SERVICE_MAP.md.
#pragma warning disable EF1001
    private bool CurrentTrackedForeignKeyIsNull(QueryContext context, object entity)
        => context.Context.GetService<IStateManager>().TryGetEntry(entity) is { } entry
            && Reference.ForeignKey.Properties.All(property => entry[property] is null);
#pragma warning restore EF1001

    private static Action<object, object> CompileFixup(INavigation navigation)
    {
        var entity = Expression.Parameter(typeof(object), "entity");
        var related = Expression.Parameter(typeof(object), "related");
        var statements = new List<Expression>();
        Add(navigation, entity, related);
        if (navigation.Inverse is { } inverse) Add(inverse, related, entity);
        statements.Add(Expression.Empty());
        return Expression.Lambda<Action<object, object>>(Expression.Block(statements), entity, related).Compile();

        void Add(INavigation nav, ParameterExpression owner, ParameterExpression value)
        {
            if (nav.IsShadowProperty()) return;
            if (nav.IsCollection)
                statements.Add(Expression.Call(Expression.Constant(nav.GetCollectionAccessor(), typeof(IClrCollectionAccessor)),
                    typeof(IClrCollectionAccessor).GetMethod(nameof(IClrCollectionAccessor.Add))!, owner, value, Expression.Constant(true)));
            else
                // EF's provider helper also handles readonly navigation backing fields.
                statements.Add(Expression.MakeMemberAccess(Expression.Convert(owner, nav.DeclaringEntityType.ClrType),
                    nav.GetMemberInfo(forMaterialization: true, forSet: true)).Assign(Expression.Convert(value, nav.TargetEntityType.ClrType)));
        }
    }

    private static Neo4jQueryTranslationException Unsupported(string message) => Neo4jQueryRefusals.Create(message);
}
