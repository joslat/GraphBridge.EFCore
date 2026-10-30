using System.Collections.ObjectModel;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace GraphBridge.EntityFrameworkCore.Update;

internal sealed record GraphIdentifiedEdgeMutation(IUpdateEntry Entry, IdentifiedEdgeBinding Binding,
    CypherMutationCommand Command, IReadOnlyList<CypherMutationCommand> LockCommands,
    IReadOnlyList<(IProperty Property, object Value)> GeneratedValues);

internal sealed class GraphIdentifiedEdgeMutationPlanner(GraphBindingCompiler compiler)
{
    public IReadOnlyList<GraphIdentifiedEdgeMutation> Plan(IList<IUpdateEntry> entries)
        => Array.AsReadOnly(entries.Where(e => e.EntityState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(PlanEntry).ToArray());

    private GraphIdentifiedEdgeMutation PlanEntry(IUpdateEntry entry)
    {
        var binding = compiler.GetIdentifiedEdge(entry.EntityType);
        if (entry.SharedIdentityEntry is not null)
            throw new NotSupportedException("Shared-identity replacement is unsupported for identified edges.");
        var identity = binding.KeyProperty.Property;
        var immutable = new[] { identity }.Concat(binding.Source.ForeignKeyProperties.Select(p => p.Property))
            .Concat(binding.Target.ForeignKeyProperties.Select(p => p.Property));
        foreach (var property in immutable)
        {
            if (entry.HasTemporaryValue(property) || entry.IsStoreGenerated(property))
                throw new InvalidOperationException($"Identified-edge key/endpoint '{property.Name}' must have a stable client value before saving.");
            if (entry.EntityState != EntityState.Added
                && !property.GetKeyValueComparer().Equals(entry.GetCurrentValue(property), entry.GetOriginalValue(property)))
                throw new NotSupportedException("Changing an identified-edge key or rebinding its endpoints is unsupported; delete the old edge and add an edge with a new application key.");
        }
        var modelKey = entry.EntityState == EntityState.Added ? entry.GetCurrentValue(identity) : entry.GetOriginalValue(identity);
        var key = binding.KeyProperty.ToStore(modelKey)
            ?? throw new InvalidOperationException("An identified edge requires an application key.");
        var source = EndpointKeys(entry, binding.Source);
        var target = EndpointKeys(entry, binding.Target);
        var changes = new Dictionary<string, object?>();
        var originals = new Dictionary<string, object?>();
        var generated = new List<(IProperty Property, object Value)>();
        foreach (var property in binding.PayloadProperties)
        {
            var metadata = property.Property;
            var version = metadata.IsConcurrencyToken && metadata.ValueGenerated == ValueGenerated.OnAddOrUpdate;
            if (version && (metadata.ClrType != typeof(long) || metadata.GetTypeMapping().Converter is not null))
                throw new NotSupportedException("Provider-generated identified-edge concurrency versions require an unconverted non-nullable long IsRowVersion property.");
            if (entry.EntityState != EntityState.Added && metadata.IsConcurrencyToken)
                originals[property.Name] = property.ToStore(entry.GetOriginalValue(metadata));
            if (entry.EntityState == EntityState.Deleted) continue;
            if (version)
            {
                var value = entry.EntityState == EntityState.Added ? 1L : checked((long)entry.GetOriginalValue(metadata)! + 1);
                changes[property.Name] = value;
                generated.Add((metadata, value));
            }
            else
            {
                if (entry.IsStoreGenerated(metadata))
                    throw new NotSupportedException($"Store-generated identified-edge property '{metadata.Name}' is unsupported.");
                if (entry.EntityState == EntityState.Added || entry.IsModified(metadata))
                    changes[property.Name] = property.ToStore(entry.GetCurrentValue(metadata));
            }
        }
        var payload = new ReadOnlyDictionary<string, object?>(changes);
        var tokens = new ReadOnlyDictionary<string, object?>(originals);
        var command = entry.EntityState switch
        {
            EntityState.Added => IdentifiedEdgeMutationRenderer.Create(binding.RelationshipType, binding.KeyProperty.Name, key,
                binding.Source.Node.Labels, source, binding.Target.Node.Labels, target, payload),
            EntityState.Modified => IdentifiedEdgeMutationRenderer.Update(binding.RelationshipType, binding.KeyProperty.Name, key,
                binding.Source.Node.Labels, source, binding.Target.Node.Labels, target, tokens, payload),
            EntityState.Deleted => IdentifiedEdgeMutationRenderer.Delete(binding.RelationshipType, binding.KeyProperty.Name, key,
                binding.Source.Node.Labels, source, binding.Target.Node.Labels, target, tokens),
            _ => throw new InvalidOperationException("Unsupported identified-edge mutation state.")
        };
        return new(entry, binding, command, Array.AsReadOnly(new[]
        {
            CypherMutationRenderer.Lock(binding.Source.Node.Labels, source),
            CypherMutationRenderer.Lock(binding.Target.Node.Labels, target)
        }), generated.AsReadOnly());
    }

    private static IReadOnlyDictionary<string, object?> EndpointKeys(IUpdateEntry entry, AssociationEndpointBinding endpoint)
        => new ReadOnlyDictionary<string, object?>(endpoint.ForeignKeyProperties.Select((property, index) =>
            new KeyValuePair<string, object?>(endpoint.PrincipalKeyProperties[index].Name,
                property.ToStore(entry.EntityState == EntityState.Added
                    ? entry.GetCurrentValue(property.Property) : entry.GetOriginalValue(property.Property))))
            .ToDictionary(p => p.Key, p => p.Value));
}
