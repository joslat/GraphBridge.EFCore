using System.Collections.ObjectModel;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace GraphBridge.EntityFrameworkCore.Update;

internal sealed record GraphAssociationMutation(IUpdateEntry Entry, AssociationEdgeBinding Binding,
    CypherMutationCommand Command, IReadOnlyList<CypherMutationCommand> LockCommands,
    IReadOnlyList<(IProperty Property, object Value)> GeneratedValues);

/// <summary>Plans an EF-owned association entry without accepting state or executing transport.</summary>
internal sealed class GraphAssociationMutationPlanner(GraphBindingCompiler compiler)
{
    public IReadOnlyList<GraphAssociationMutation> Plan(IList<IUpdateEntry> entries)
        => Array.AsReadOnly(entries.Where(e => e.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(PlanEntry).ToArray());

    private GraphAssociationMutation PlanEntry(IUpdateEntry entry)
    {
        var binding = compiler.GetAssociation(entry.EntityType);
        if (entry.SharedIdentityEntry is not null)
            throw new NotSupportedException("Shared-identity entries are unsupported for association mutations; save the deletion before tracking a replacement with the same key.");

        foreach (var key in binding.KeyProperties)
        {
            if (entry.HasTemporaryValue(key))
                throw new InvalidOperationException($"GraphBridge association endpoint key '{key.Name}' is temporary; stable endpoint keys are required before planning.");
            if (entry.EntityState != EntityState.Added
                && !key.GetKeyValueComparer().Equals(entry.GetCurrentValue(key), entry.GetOriginalValue(key)))
                throw new InvalidOperationException($"GraphBridge association key '{key.Name}' changed; replace an association using a deleted old join and an added new join.");
        }

        var sourceKeys = EndpointKeys(entry, binding.Source);
        var targetKeys = EndpointKeys(entry, binding.Target);
        var changes = new Dictionary<string, object?>();
        var originals = new Dictionary<string, object?>();
        var generated = new List<(IProperty Property, object Value)>();
        foreach (var property in binding.PayloadProperties)
        {
            var metadata = property.Property;
            var numericVersion = metadata.IsConcurrencyToken && metadata.ValueGenerated == ValueGenerated.OnAddOrUpdate;
            if (numericVersion && (metadata.ClrType != typeof(long) || metadata.GetTypeMapping().Converter is not null))
                throw new NotSupportedException("Provider-generated association concurrency versions require an unconverted non-nullable long IsRowVersion property.");
            if (entry.EntityState != EntityState.Added && metadata.IsConcurrencyToken)
                originals[property.Name] = property.ToStore(entry.GetOriginalValue(metadata));
            if (entry.EntityState == EntityState.Deleted)
                continue;
            if (numericVersion)
            {
                var value = entry.EntityState == EntityState.Added ? 1L : checked((long)entry.GetOriginalValue(metadata)! + 1);
                changes[property.Name] = value;
                generated.Add((metadata, value));
            }
            else
            {
                if (entry.IsStoreGenerated(metadata))
                    throw new NotSupportedException($"Store-generated association property '{metadata.Name}' is unsupported; use a client value or long IsRowVersion.");
                if (entry.EntityState == EntityState.Added || entry.IsModified(metadata))
                    changes[property.Name] = property.ToStore(entry.GetCurrentValue(metadata));
            }
        }

        var sourceLabel = binding.Source.Node.Labels;
        var targetLabel = binding.Target.Node.Labels;
        var values = new ReadOnlyDictionary<string, object?>(changes);
        var originalValues = new ReadOnlyDictionary<string, object?>(originals);
        var command = entry.EntityState switch
        {
            EntityState.Added => CypherAssociationMutationRenderer.Merge(binding.RelationshipType,
                sourceLabel, sourceKeys, targetLabel, targetKeys, values),
            EntityState.Modified => CypherAssociationMutationRenderer.Update(binding.RelationshipType,
                sourceLabel, sourceKeys, targetLabel, targetKeys, originalValues, values),
            EntityState.Deleted => CypherAssociationMutationRenderer.Delete(binding.RelationshipType,
                sourceLabel, sourceKeys, targetLabel, targetKeys, originalValues),
            _ => throw new InvalidOperationException("Unsupported association mutation state.")
        };
        // Each lock and the mutation must independently return cardinality one in a future executor.
        // Source-then-target ordering is explicit, but is not a promise of cross-context deadlock prevention.
        return new GraphAssociationMutation(entry, binding, command, Array.AsReadOnly(new[]
        {
            CypherMutationRenderer.Lock(sourceLabel, sourceKeys),
            CypherMutationRenderer.Lock(targetLabel, targetKeys)
        }), generated.AsReadOnly());
    }

    private static IReadOnlyDictionary<string, object?> EndpointKeys(IUpdateEntry entry, AssociationEndpointBinding endpoint)
    {
        var keys = new Dictionary<string, object?>();
        for (var index = 0; index < endpoint.ForeignKeyProperties.Count; index++)
        {
            var dependent = endpoint.ForeignKeyProperties[index];
            var value = entry.EntityState == EntityState.Added
                ? entry.GetCurrentValue(dependent.Property) : entry.GetOriginalValue(dependent.Property);
            keys.Add(endpoint.PrincipalKeyProperties[index].Name, dependent.ToStore(value));
        }
        return new ReadOnlyDictionary<string, object?>(keys);
    }
}
