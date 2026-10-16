using System.Collections.ObjectModel;
using System.Text;

namespace GraphBridge.EntityFrameworkCore.Migrations;

public sealed class GraphMigrationCommand
{
    internal GraphMigrationCommand(string cypher, IDictionary<string, object?> parameters)
    {
        Cypher = cypher;
        Parameters = new ReadOnlyDictionary<string, object?>(new SortedDictionary<string, object?>(parameters, StringComparer.Ordinal));
    }
    public string Cypher { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
}

/// <summary>Pure rendering of typed steps. Executing a command alone does not implement migration ownership or journaling.</summary>
public static class GraphMigrationStepRenderer
{
    public static GraphMigrationCommand Render(GraphMigrationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Schema is not null ? Schema(step) : Data(step, mutate: step.Kind != GraphMigrationStepKind.AssertPrecondition);
    }

    /// <summary>A read-only, advisory count and eligibility observation. Apply rechecks candidates under write locks; schema conditions require an exact SHOW observation.</summary>
    public static GraphMigrationCommand RenderPrecondition(GraphMigrationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Schema is not null) throw new ArgumentException("Schema preconditions require exact schema observations.", nameof(step));
        return Data(step, mutate: false);
    }

    private static GraphMigrationCommand Schema(GraphMigrationStep step)
    {
        var schema = step.Schema!;
        var kind = schema.IsIndex ? "INDEX" : "CONSTRAINT";
        if (step.Kind is GraphMigrationStepKind.DropConstraint or GraphMigrationStepKind.DropIndex)
            return new($"DROP {kind} `{schema.Name}`", new Dictionary<string, object?>());
        var variable = schema.EntityType == "NODE" ? "n" : "r";
        var pattern = schema.EntityType == "NODE" ? $"(n:`{schema.LabelOrType}`)" : $"()-[r:`{schema.LabelOrType}`]-()";
        var slots = string.Join(", ", schema.Properties.Select(p => $"{variable}.`{p}`"));
        var cypher = schema.Kind switch
        {
            GraphMigrationSchemaKind.RangeIndex => $"CREATE RANGE INDEX `{schema.Name}` FOR {pattern} ON ({slots})",
            GraphMigrationSchemaKind.UniquenessConstraint => $"CREATE CONSTRAINT `{schema.Name}` FOR {pattern} REQUIRE ({slots}) IS UNIQUE",
            GraphMigrationSchemaKind.ExistenceConstraint => $"CREATE CONSTRAINT `{schema.Name}` FOR {pattern} REQUIRE {slots} IS NOT NULL",
            GraphMigrationSchemaKind.PropertyTypeConstraint => $"CREATE CONSTRAINT `{schema.Name}` FOR {pattern} REQUIRE {slots} IS :: {schema.PropertyType}",
            _ => throw new InvalidOperationException("Unsupported schema operation.")
        };
        return new(cypher, new Dictionary<string, object?>());
    }

    private static GraphMigrationCommand Data(GraphMigrationStep step, bool mutate)
    {
        var scope = step.Scope!;
        var parameters = new Dictionary<string, object?>
        {
            ["maxRows"] = (long)scope.MaxRows,
            ["limit"] = (long)scope.MaxRows + 1,
            ["expected"] = step.ExpectedCount
        };
        var text = new StringBuilder(scope.EntityType == "NODE"
            ? $"MATCH (e:`{scope.LabelOrType}`)" : $"MATCH ()-[e:`{scope.LabelOrType}`]->()");
        var predicates = new List<string>();
        var currentPredicates = new List<string>();
        if (scope.EntityType == "NODE") currentPredicates.Add($"e:`{scope.LabelOrType}`");
        foreach (var pair in scope.Filters)
        {
            var name = "filter" + predicates.Count;
            parameters.Add(name, pair.Value);
            var property = $"e.`{pair.Key}`";
            predicates.Add($"coalesce({property} = ${name}, ({property} IS NULL AND ${name} IS NULL))");
            var currentProperty = $"properties(e).`{pair.Key}`";
            currentPredicates.Add($"coalesce({currentProperty} = ${name}, ({currentProperty} IS NULL AND ${name} IS NULL))");
        }
        if (predicates.Count > 0) text.Append("\nWHERE ").AppendJoin(" AND ", predicates);
        // LIMIT belongs to a separate read query part before all updates. collect is bounded by MaxRows+1.
        // Neo4j FOREACH permits SET and REMOVE in one body; there are no procedures or subtransactions.
        text.Append("\nWITH DISTINCT e LIMIT $limit\nWITH collect(e) AS rows");
        if (mutate)
        {
            // A dependent SET locks each entity before reading its current value without changing it.
            // Read scope/eligibility afterwards via properties(e), avoiding pre-lock cached property slots.
            // https://neo4j.com/docs/operations-manual/current/database-internals/concurrent-data-access/
            text.Append("\nFOREACH (e IN rows | SET e.`").Append(step.TargetProperty).Append("` = e.`")
                .Append(step.TargetProperty).Append("`)");
        }
        switch (step.Kind)
        {
            case GraphMigrationStepKind.RenameProperty:
                currentPredicates.Add($"properties(e).`{step.SourceProperty}` IS NOT NULL");
                currentPredicates.Add($"properties(e).`{step.TargetProperty}` IS NULL");
                break;
            case GraphMigrationStepKind.Backfill:
                currentPredicates.Add($"properties(e).`{step.TargetProperty}` IS NULL");
                break;
            case GraphMigrationStepKind.AssertPrecondition:
                break;
            default:
                throw new InvalidOperationException("Unsupported data operation.");
        }
        text.Append("\nWITH rows, size(rows) AS matched, ");
        if (currentPredicates.Count == 0) text.Append("size(rows)");
        else text.Append("size([e IN rows WHERE ").AppendJoin(" AND ", currentPredicates).Append("])");
        text.Append(" AS eligible");
        const string guard = "matched = $expected AND matched <= $maxRows AND eligible = matched";
        if (mutate)
        {
            text.Append("\nFOREACH (e IN CASE WHEN ").Append(guard).Append(" THEN rows ELSE [] END |\n  SET e.`")
                .Append(step.TargetProperty).Append("` = ");
            if (step.Kind == GraphMigrationStepKind.RenameProperty)
                text.Append("properties(e).`").Append(step.SourceProperty).Append("`\n  REMOVE e.`").Append(step.SourceProperty).Append('`');
            else
            {
                text.Append("$value");
                parameters.Add("value", step.Value);
            }
            text.Append("\n)\nRETURN matched, eligible, CASE WHEN ").Append(guard).Append(" THEN matched ELSE 0 END AS affected");
        }
        else text.Append("\nRETURN matched, eligible, 0 AS affected");
        return new(text.ToString(), parameters);
    }
}
