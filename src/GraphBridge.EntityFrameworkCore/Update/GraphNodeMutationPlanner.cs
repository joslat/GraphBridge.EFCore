using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace GraphBridge.EntityFrameworkCore.Update;

internal sealed record GraphNodeMutation(IUpdateEntry Entry, NodeBinding Binding,
    CypherMutationCommand Command, CypherMutationCommand? LockCommand,
    IReadOnlyList<(IProperty Property, object Value)> GeneratedValues);

internal sealed class GraphNodeMutationPlanner(GraphBindingCompiler compiler)
{
    public IReadOnlyList<GraphNodeMutation> Plan(IList<IUpdateEntry> entries)
        => entries.Where(e => e.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(PlanEntry).ToArray();

    private GraphNodeMutation PlanEntry(IUpdateEntry entry)
    {
        var binding = compiler.GetNode(entry.EntityType);
        if (entry.SharedIdentityEntry is not null)
            throw new NotSupportedException("Shared-identity entries are unsupported for node mutations.");
        var properties = binding.StoredProperties;
        var keys = new Dictionary<string, object?>();
        foreach (var key in binding.KeyProperties)
        {
            if (entry.HasTemporaryValue(key))
                throw new InvalidOperationException($"GraphBridge key '{key.Name}' is temporary. Supply a stable application key or use EF client-generated Guid keys.");
            var property = properties.Single(p => p.Property == key);
            keys[property.Name] = property.ToStore(entry.EntityState == EntityState.Added
                ? entry.GetCurrentValue(key) : entry.GetOriginalValue(key));
        }

        var changes = new Dictionary<string, object?>();
        var originals = new Dictionary<string, object?>();
        var generated = new List<(IProperty Property, object Value)>();
        foreach (var property in properties)
        {
            var metadata = property.Property;
            var numericVersion = metadata.IsConcurrencyToken && metadata.ValueGenerated == ValueGenerated.OnAddOrUpdate;
            if (numericVersion && (metadata.ClrType != typeof(long) || metadata.GetTypeMapping().Converter is not null))
                throw new NotSupportedException("Provider-generated concurrency versions require an unconverted non-nullable long IsRowVersion property.");
            if (numericVersion && binding.KeyProperties.Contains(metadata))
                throw new NotSupportedException("A provider-generated concurrency version cannot be part of the stable application key.");
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
                    throw new NotSupportedException($"Store-generated property '{metadata.Name}' is unsupported; use a client value or long IsRowVersion.");
                if (entry.EntityState == EntityState.Added || entry.IsModified(metadata))
                    changes[property.Name] = property.ToStore(entry.GetCurrentValue(metadata));
            }
        }
        var command = entry.EntityState switch
        {
            EntityState.Added => CypherMutationRenderer.Create(binding.Label, changes),
            EntityState.Modified => CypherMutationRenderer.Update(binding.Label, keys, originals, changes),
            EntityState.Deleted => CypherMutationRenderer.Delete(binding.Label, keys, originals),
            _ => throw new InvalidOperationException("Unsupported mutation state.")
        };
        return new(entry, binding, command, entry.EntityState == EntityState.Added ? null
            : CypherMutationRenderer.Lock(binding.Label, keys), generated.AsReadOnly());
    }
}
