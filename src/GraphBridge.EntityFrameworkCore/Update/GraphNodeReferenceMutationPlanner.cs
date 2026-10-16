using System.Collections.ObjectModel;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace GraphBridge.EntityFrameworkCore.Update;

internal sealed record GraphNodeReferenceMutation(IUpdateEntry Entry, NodeReferenceBinding Binding,
    CypherMutationCommand Command, IReadOnlyList<CypherMutationCommand> LockCommands);

/// <summary>Plans only native membership. Scalar values, concurrency and generated versions remain owned by the node planner.</summary>
internal sealed class GraphNodeReferenceMutationPlanner(GraphBindingCompiler compiler)
{
    public IReadOnlyList<GraphNodeReferenceMutation> Plan(IList<IUpdateEntry> entries)
        => Array.AsReadOnly(entries.Where(entry => entry.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .SelectMany(entry => compiler.GetNodeReferences(entry.EntityType).Select(binding => Plan(entry, binding))).ToArray());

    private static GraphNodeReferenceMutation Plan(IUpdateEntry entry, NodeReferenceBinding binding)
    {
        if (!binding.ForeignKey.IsRequired) return PlanOptional(entry, binding);
        var principalKeys = new Dictionary<string, object?>();
        for (var index = 0; index < binding.ForeignKeyProperties.Count; index++)
        {
            var property = binding.ForeignKeyProperties[index];
            if (entry.HasTemporaryValue(property.Property))
                throw new InvalidOperationException($"GraphBridge native reference FK '{property.Property.Name}' is temporary; stable principal keys are required before planning.");
            var current = entry.GetCurrentValue(property.Property);
            if (current is null || entry.IsConceptualNull(property.Property))
                throw new InvalidOperationException($"GraphBridge required native reference FK '{property.Property.Name}' is null or conceptually null.");
            var original = entry.EntityState == EntityState.Added ? current : entry.GetOriginalValue(property.Property);
            if (entry.EntityState != EntityState.Added && !property.Property.GetKeyValueComparer().Equals(current, original))
                throw new NotSupportedException($"GraphBridge native reference FK '{property.Property.Name}' cannot be reassigned; the existing principal is immutable in this runtime contract.");
            principalKeys.Add(binding.PrincipalKeyProperties[index].Name, property.ToStore(original));
        }
        var principal = new ReadOnlyDictionary<string, object?>(principalKeys);
        var dependent = DependentKeys(entry, binding);
        var command = entry.EntityState switch
        {
            EntityState.Added => CypherNodeReferenceMutationRenderer.Create(binding.RelationshipType, binding.Principal.Label, principal, binding.Dependent.Label, dependent),
            EntityState.Modified => CypherNodeReferenceMutationRenderer.Assert(binding.RelationshipType, binding.Principal.Label, principal, binding.Dependent.Label, dependent),
            EntityState.Deleted => CypherNodeReferenceMutationRenderer.Delete(binding.RelationshipType, binding.Principal.Label, principal, binding.Dependent.Label, dependent),
            _ => throw new InvalidOperationException("Unsupported native reference mutation state.")
        };
        return new(entry, binding, command, Array.AsReadOnly(new[]
        {
            CypherMutationRenderer.Lock(binding.Dependent.Label, dependent),
            CypherMutationRenderer.Lock(binding.Principal.Label, principal)
        }));
    }

    private static GraphNodeReferenceMutation PlanOptional(IUpdateEntry entry, NodeReferenceBinding binding)
    {
        var dependent = DependentKeys(entry, binding);
        var original = entry.EntityState == EntityState.Added ? null : PrincipalKeys(originalValues: true);
        var current = entry.EntityState == EntityState.Deleted ? null : PrincipalKeys(originalValues: false);
        var unchanged = original is not null && current is not null && original.SequenceEqual(current);
        var command = unchanged
            ? CypherNodeReferenceMutationRenderer.Assert(binding.RelationshipType, binding.Principal.Label, original!, binding.Dependent.Label, dependent)
            : CypherNodeReferenceMutationRenderer.TransitionOptional(binding.RelationshipType, binding.Principal.Label, original, current, binding.Dependent.Label, dependent);
        var locks = new List<CypherMutationCommand> { CypherMutationRenderer.Lock(binding.Dependent.Label, dependent) };
        if (original is not null) locks.Add(CypherMutationRenderer.Lock(binding.Principal.Label, original));
        if (current is not null && !unchanged) locks.Add(CypherMutationRenderer.Lock(binding.Principal.Label, current));
        return new(entry, binding, command, locks.AsReadOnly());

        IReadOnlyDictionary<string, object?>? PrincipalKeys(bool originalValues)
        {
            var values = binding.ForeignKeyProperties.Select(property =>
            {
                if (!originalValues && entry.HasTemporaryValue(property.Property))
                    throw new InvalidOperationException("GraphBridge optional reference requires stable principal keys before planning.");
                return originalValues ? entry.GetOriginalValue(property.Property)
                    : entry.IsConceptualNull(property.Property) ? null : entry.GetCurrentValue(property.Property);
            }).ToArray();
            if (values.All(value => value is null)) return null;
            if (values.Any(value => value is null))
                throw new InvalidOperationException("GraphBridge optional composite reference FK must be entirely null or fully specified.");
            var keys = new Dictionary<string, object?>();
            for (var i = 0; i < values.Length; i++)
                keys.Add(binding.PrincipalKeyProperties[i].Name, binding.ForeignKeyProperties[i].ToStore(values[i]));
            return new ReadOnlyDictionary<string, object?>(keys);
        }
    }

    private static IReadOnlyDictionary<string, object?> DependentKeys(IUpdateEntry entry, NodeReferenceBinding binding)
    {
        var dependentKeys = new Dictionary<string, object?>();
        foreach (var key in binding.Dependent.KeyProperties)
        {
            if (entry.HasTemporaryValue(key))
                throw new InvalidOperationException($"GraphBridge dependent key '{key.Name}' is temporary; stable node keys are required before planning.");
            var property = binding.Dependent.StoredProperties.Single(property => property.Property == key);
            dependentKeys.Add(property.Name, property.ToStore(entry.EntityState == EntityState.Added ? entry.GetCurrentValue(key) : entry.GetOriginalValue(key)));
        }
        return new ReadOnlyDictionary<string, object?>(dependentKeys);
    }
}
