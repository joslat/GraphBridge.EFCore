using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace GraphBridge.EntityFrameworkCore.Update;

internal enum GraphMutationPhase
{
    NodeCreate,
    NodeReferenceAdd,
    NodeUpdate,
    AssociationDelete,
    AssociationUpdate,
    AssociationAdd,
    NodeDelete
}

// LockCommands run before Command. For a changed node with a native reference, the last of them checks its reference's
// membership instead of locking (ChecksReference), so a failure there is named as such.
internal sealed record GraphMutationOperation(IUpdateEntry Entry, GraphMutationPhase Phase,
    CypherMutationCommand Command, IReadOnlyList<CypherMutationCommand> LockCommands,
    IReadOnlyList<(IProperty Property, object Value)> GeneratedValues, bool ChecksReference = false);

internal sealed record GraphGeneratedValue(IUpdateEntry Entry, IProperty Property, object Value);

internal sealed class GraphMutationPlan
{
    internal GraphMutationPlan(GraphMutationOperation[] operations, NodeBinding[] requiredNodeBindings,
        IdentifiedEdgeBinding[] requiredIdentifiedEdges)
    {
        Operations = Array.AsReadOnly(operations);
        RequiredNodeBindings = Array.AsReadOnly(requiredNodeBindings);
        RequiredIdentifiedEdges = Array.AsReadOnly(requiredIdentifiedEdges);
        GeneratedValues = Array.AsReadOnly(operations.SelectMany(operation => operation.GeneratedValues
            .Select(value => new GraphGeneratedValue(operation.Entry, value.Property, value.Value))).ToArray());
    }

    public IReadOnlyList<GraphMutationOperation> Operations { get; }
    public IReadOnlyList<NodeBinding> RequiredNodeBindings { get; }
    public IReadOnlyList<IdentifiedEdgeBinding> RequiredIdentifiedEdges { get; }
    public IReadOnlyList<GraphGeneratedValue> GeneratedValues { get; }
    public int ExpectedStateEntries => Operations.Select(operation => operation.Entry).Distinct<IUpdateEntry>(ReferenceEqualityComparer.Instance).Count();
}

/// <summary>
/// Composes node and association planning for the transactional save executor.
/// Phases express storage dependencies; entries retain input order within a phase.
/// </summary>
internal sealed class GraphMutationPlanner(GraphBindingCompiler compiler)
{
    public GraphMutationPlan Plan(IList<IUpdateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var changed = entries.Where(e => e.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        var inputIndices = new Dictionary<IUpdateEntry, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < changed.Length; index++)
            if (!inputIndices.TryAdd(changed[index], index))
                throw new InvalidOperationException("A mutation plan cannot contain the same EF update entry more than once.");
        var nodeEntries = new List<IUpdateEntry>();
        var associationEntries = new List<IUpdateEntry>();
        var identifiedEntries = new List<IUpdateEntry>();
        foreach (var entry in changed)
        {
            var kind = compiler.GetStorageKind(entry.EntityType);
            if (kind == GraphStorageKind.Node)
                nodeEntries.Add(entry);
            else if (kind == GraphStorageKind.AssociationEdge)
                associationEntries.Add(entry);
            else identifiedEntries.Add(entry);
        }

        // Validate/plan every entry before any consumer could execute an operation.
        var references = new GraphNodeReferenceMutationPlanner(compiler).Plan(nodeEntries);
        var nodes = new GraphNodeMutationPlanner(compiler).Plan(nodeEntries);
        var associations = new GraphAssociationMutationPlanner(compiler).Plan(associationEntries);
        var identified = new GraphIdentifiedEdgeMutationPlanner(compiler).Plan(identifiedEntries);
        var byEntry = new Dictionary<IUpdateEntry, GraphNodeReferenceMutation>(ReferenceEqualityComparer.Instance);
        foreach (var reference in references) byEntry.Add(reference.Entry, reference);
        var operations = nodes.Select(node => new GraphMutationOperation(node.Entry, NodePhase(node.Entry.EntityState),
                node.Command, NodePreparations(node), node.GeneratedValues,
                ChecksReference: node.Entry.EntityState != EntityState.Added && byEntry.ContainsKey(node.Entry)))
            .Concat(references.Where(reference => reference.Entry.EntityState == EntityState.Added)
                .Select(reference => new GraphMutationOperation(reference.Entry, GraphMutationPhase.NodeReferenceAdd,
                    reference.Command, reference.LockCommands, Array.Empty<(IProperty, object)>())))
            .Concat(associations.Select(association => new GraphMutationOperation(association.Entry,
                AssociationPhase(association.Entry.EntityState), association.Command, association.LockCommands, association.GeneratedValues)))
            .Concat(identified.Select(edge => new GraphMutationOperation(edge.Entry,
                AssociationPhase(edge.Entry.EntityState), edge.Command, edge.LockCommands, edge.GeneratedValues)))
            .OrderBy(operation => operation.Phase)
            .ThenBy(operation => operation.Phase == GraphMutationPhase.NodeDelete ? -ReferenceDepth(operation.Entry.EntityType) : 0)
            .ThenBy(operation => inputIndices[operation.Entry])
            .ToArray();
        var requiredNodes = nodes.Select(node => node.Binding)
            .Concat(references.SelectMany(reference => new[] { reference.Binding.Dependent, reference.Binding.Principal }))
            .Concat(associations.SelectMany(association => new[] { association.Binding.Source.Node, association.Binding.Target.Node }))
            .Concat(identified.SelectMany(edge => new[] { edge.Binding.Source.Node, edge.Binding.Target.Node }))
            .Distinct()
            .OrderBy(node => node.Label, StringComparer.Ordinal)
            .ToArray();
        return new GraphMutationPlan(operations, requiredNodes, identified.Select(edge => edge.Binding).Distinct().ToArray());

        IReadOnlyList<CypherMutationCommand> NodePreparations(GraphNodeMutation node)
        {
            if (node.Entry.EntityState != EntityState.Added && byEntry.TryGetValue(node.Entry, out var reference))
                // The dependent lock also covers ordinary node concurrency; membership is asserted/removed before
                // the existing scalar command. Any later stale token or restrictive DELETE failure rolls back both.
                return Array.AsReadOnly(reference.LockCommands.Append(reference.Command).ToArray());
            return Array.AsReadOnly(node.LockCommand is null ? [] : new[] { node.LockCommand });
        }

        int ReferenceDepth(IEntityType entity)
        {
            var depth = 0;
            while (compiler.GetNodeReferences(entity).SingleOrDefault() is { } reference)
            {
                depth++;
                entity = reference.Principal.EntityType;
            }
            return depth;
        }
    }

    private static GraphMutationPhase NodePhase(EntityState state) => state switch
    {
        EntityState.Added => GraphMutationPhase.NodeCreate,
        EntityState.Modified => GraphMutationPhase.NodeUpdate,
        EntityState.Deleted => GraphMutationPhase.NodeDelete,
        _ => throw new InvalidOperationException("Unsupported node mutation state.")
    };

    private static GraphMutationPhase AssociationPhase(EntityState state) => state switch
    {
        EntityState.Added => GraphMutationPhase.AssociationAdd,
        EntityState.Modified => GraphMutationPhase.AssociationUpdate,
        EntityState.Deleted => GraphMutationPhase.AssociationDelete,
        _ => throw new InvalidOperationException("Unsupported association mutation state.")
    };
}
