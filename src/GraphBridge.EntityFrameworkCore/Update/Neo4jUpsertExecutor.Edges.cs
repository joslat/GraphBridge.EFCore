using System.Linq.Expressions;
using System.Text;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Update;

// ADR 0051, section 6: relationship upserts between existing endpoints. A preflight in the same transaction finds and
// locks the endpoints and counts conflicting relationships; the MERGE then matches on the edge key or the endpoint pair.
internal static partial class Neo4jUpsertExecutor
{
    private static GraphUpsertCommand PrepareEdge(GraphBindingCompiler compiler, IEntityType entityType, GraphStorageKind kind,
        IEnumerable<object> entities, LambdaExpression? updateProperties)
    {
        IdentifiedEdgeBinding? identified = null;
        AssociationEndpointBinding source, target;
        IReadOnlyList<GraphPropertyBinding> payload;
        string type;
        if (kind == GraphStorageKind.IdentifiedEdge)
        {
            identified = compiler.GetIdentifiedEdge(entityType);
            (type, source, target, payload) = (identified.RelationshipType, identified.Source, identified.Target, identified.PayloadProperties);
        }
        else
        {
            var association = compiler.GetAssociation(entityType);
            (type, source, target, payload) = (association.RelationshipType, association.Source, association.Target, association.PayloadProperties);
        }
        var endpointProperties = source.ForeignKeyProperties.Concat(target.ForeignKeyProperties).ToArray();
        var read = endpointProperties.Concat(payload).Concat(identified is null ? [] : [identified.KeyProperty]).ToArray();
        if (read.FirstOrDefault(p => p.Property.IsConcurrencyToken) is { } token)
            throw new NotSupportedException($"Upserts don't support concurrency tokens yet ('{entityType.DisplayName()}.{token.Property.Name}').");
        if (read.FirstOrDefault(p => p.Property.IsShadowProperty()) is { } shadow)
            throw new NotSupportedException($"Upserts read values from the entities passed in; shadow property '{entityType.DisplayName()}.{shadow.Property.Name}' has none. Map the relationship with an explicit join entity type.");
        var reserved = endpointProperties.Select(p => p.Property.Name).Concat(identified is null ? [] : [identified.KeyProperty.Property.Name])
            .ToHashSet(StringComparer.Ordinal);
        var update = UpdateSet(payload, entityType, updateProperties, reserved, "identifies the relationship or names an endpoint");

        var rows = new List<Dictionary<string, object?>>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (entity is null) throw new ArgumentException("Upsert entities cannot be null.", nameof(entities));
            if (rows.Count == Neo4jBulkExpression.MaxRows)
                throw new InvalidOperationException($"Upserts support at most {Neo4jBulkExpression.MaxRows} entities per atomic call; nothing was sent. Split the input into several calls.");
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["position"] = (long)rows.Count,
                ["source"] = Endpoint(source, entity, entityType, rows.Count),
                ["target"] = Endpoint(target, entity, entityType, rows.Count)
            };
            string identity;
            if (identified is not null)
            {
                var value = Value(identified.KeyProperty, entity);
                if (Equals(value, identified.KeyProperty.Property.Sentinel))
                    throw new InvalidOperationException($"Upsert key '{entityType.DisplayName()}.{identified.KeyProperty.Property.Name}' has its default value. Upserts need client-supplied keys.");
                var key = new Dictionary<string, object?>(StringComparer.Ordinal) { [identified.KeyProperty.Name] = identified.KeyProperty.ToStore(value) };
                row["key"] = key;
                identity = Identity(key);
            }
            else identity = Identity((Dictionary<string, object?>)row["source"]!) + "\u001f" + Identity((Dictionary<string, object?>)row["target"]!);
            // Positions only: keys and endpoint values stay out of exception messages and logs.
            if (!seen.TryAdd(identity, rows.Count))
                throw new InvalidOperationException(identified is not null
                    ? $"Entities {seen[identity]} and {rows.Count} (zero-based) in one upsert have the same key; nothing was sent. Remove the duplicate: an upsert never chooses between them."
                    : $"Entities {seen[identity]} and {rows.Count} (zero-based) in one upsert link the same endpoints; nothing was sent. An association has one relationship per pair.");
            var create = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in payload)
                if (StoreAt(property, Value(property, entity), rows.Count) is { } stored)
                    create[property.Name] = stored;
            row["create"] = create;
            if (update.Count != 0)
            {
                var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in update) changes[property.Name] = StoreAt(property, Value(property, entity), rows.Count);
                row["update"] = changes;
            }
            rows.Add(row);
        }

        var match = new StringBuilder("UNWIND $").Append(RowsParameter).Append(" AS row");
        var relationship = "[r:" + CypherQueryRenderer.Identifier(type)
            + (identified is null ? "" : " {" + Slot(identified.KeyProperty.Name, "key") + "}") + "]";
        var text = new StringBuilder(match.ToString())
            .Append("\nMATCH (s:").Append(CypherQueryRenderer.Labels(source.Node.Labels)).Append(" {").Append(Slots(source, "source")).Append("})")
            .Append("\nMATCH (t:").Append(CypherQueryRenderer.Labels(target.Node.Labels)).Append(" {").Append(Slots(target, "target")).Append("})")
            .Append("\nMERGE (s)-").Append(relationship).Append("->(t)")
            .Append("\nON CREATE SET r += row.create");
        if (update.Count != 0) text.Append("\nON MATCH SET r += row.update");
        text.Append("\nRETURN count(r) AS affected");

        // Endpoint write locks, taken as SaveChanges takes them, so concurrent upserts serialize: a transient property, never a key
        // written to itself, which on Neo4j 2026.09 would keep the key taken if the node is deleted and added again later in the
        // same transaction.
        var preflight = new StringBuilder(match.ToString())
            .Append("\nOPTIONAL MATCH (s:").Append(CypherQueryRenderer.Labels(source.Node.Labels)).Append(" {").Append(Slots(source, "source")).Append("})")
            .Append("\nOPTIONAL MATCH (t:").Append(CypherQueryRenderer.Labels(target.Node.Labels)).Append(" {").Append(Slots(target, "target")).Append("})")
            .Append("\nFOREACH (x IN CASE WHEN s IS NULL THEN [] ELSE [s] END | ").Append(CypherMutationRenderer.LockWrite("x")).Append(')')
            .Append("\nFOREACH (x IN CASE WHEN t IS NULL THEN [] ELSE [t] END | ").Append(CypherMutationRenderer.LockWrite("x")).Append(')')
            .Append("\nWITH row, s, t");
        if (identified is not null)
            // The key on a relationship between other endpoints: a MERGE would try to create it again, and the constraint refuses.
            preflight.Append("\nOPTIONAL MATCH (a)-[e:").Append(CypherQueryRenderer.Identifier(type)).Append(" {").Append(Slot(identified.KeyProperty.Name, "key"))
                .Append("}]->(b) WHERE s IS NULL OR t IS NULL OR a <> s OR b <> t");
        else
            preflight.Append("\nOPTIONAL MATCH (s)-[e:").Append(CypherQueryRenderer.Identifier(type)).Append("]->(t) WHERE s IS NOT NULL AND t IS NOT NULL");
        preflight.Append("\nRETURN row.position AS position, s IS NOT NULL AS source, t IS NOT NULL AS target, count(e) AS edges");

        return new([source.Node, target.Node], identified, preflight.ToString(), text.ToString(),
            new Dictionary<string, object> { [RowsParameter] = rows.ToArray() }, rows.Count, Relationship: true);
    }

    // A row's endpoint: the principal key's stored names, with the foreign key's values converted as SaveChanges converts them.
    private static Dictionary<string, object?> Endpoint(AssociationEndpointBinding endpoint, object entity, IEntityType entityType, int position)
    {
        var keys = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < endpoint.ForeignKeyProperties.Count; i++)
        {
            var value = Value(endpoint.ForeignKeyProperties[i], entity);
            if (value is null)
                throw new InvalidOperationException($"Entity {position} (zero-based) in one upsert has no value for foreign key '{entityType.DisplayName()}.{endpoint.ForeignKeyProperties[i].Property.Name}'; nothing was sent.");
            keys[endpoint.PrincipalKeyProperties[i].Name] = endpoint.ForeignKeyProperties[i].ToStore(value);
        }
        return keys;
    }

    private static string Slots(AssociationEndpointBinding endpoint, string map)
        => string.Join(", ", endpoint.PrincipalKeyProperties.Select(p => Slot(p.Name, map)));

    private static string Slot(string name, string map)
        => CypherQueryRenderer.Identifier(name) + ": row." + map + "." + CypherQueryRenderer.Identifier(name);

    // Refuses the whole call when the preflight found a missing endpoint or a conflicting relationship; positions only.
    private static void CheckPreflight(GraphUpsertCommand command, IReadOnlyList<IRecord> records)
    {
        if (records.Count != command.Rows)
            throw new InvalidOperationException("The upsert preflight did not return one result per entity; nothing was written.");
        string Positions(Func<IRecord, bool> failing)
        {
            var positions = records.Where(failing).Select(r => r["position"].As<long>()).Order().ToArray();
            return string.Join(", ", positions.Take(10)) + (positions.Length > 10 ? $" and {positions.Length - 10} more" : "");
        }
        if (records.Any(r => !r["source"].As<bool>() || !r["target"].As<bool>()))
            throw new InvalidOperationException($"Entities {Positions(r => !r["source"].As<bool>() || !r["target"].As<bool>())} (zero-based) in one upsert name an endpoint node that doesn't exist, or lacks its type's labels; nothing was written. An upsert links existing nodes only.");
        if (command.IdentifiedEdge is not null && records.Any(r => r["edges"].As<long>() > 0))
            throw new InvalidOperationException($"Entities {Positions(r => r["edges"].As<long>() > 0)} (zero-based) in one upsert have a key that already belongs to a relationship between other endpoints; nothing was written. An upsert never moves a relationship.");
        if (command.IdentifiedEdge is null && records.Any(r => r["edges"].As<long>() > 1))
            throw new InvalidOperationException($"Entities {Positions(r => r["edges"].As<long>() > 1)} (zero-based) in one upsert link endpoints that already have more than one such relationship; nothing was written. Remove the duplicates first.");
    }
}
