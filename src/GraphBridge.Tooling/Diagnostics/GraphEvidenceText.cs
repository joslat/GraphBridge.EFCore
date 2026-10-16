using System.Globalization;
using System.Text;

namespace GraphBridge.Tooling.Diagnostics;

internal static class GraphEvidenceText
{
    internal static string Format(GraphEvidenceReport report)
    {
        var output = new StringBuilder();
        output.AppendLine("GraphBridge evidence / format 1")
            .Append("Mode: ").Append(report.Mode.ToString().ToLowerInvariant()).Append(" | Status: ").AppendLine(Status(report.Status))
            .Append("Database observation: ").AppendLine(Status(report.DatabaseObservation))
            .AppendLine("Parameter values: excluded")
            .AppendLine(report.SensitiveTextIncluded ? "SENSITIVE TEXT INCLUDED: inspect schema identifiers before sharing." : "Statement and schema text: excluded")
            .Append("Loaded provider: ").AppendLine(report.Versions.Provider)
            .Append("Loaded EF Core: ").AppendLine(report.Versions.EfCore)
            .Append("Loaded driver: ").AppendLine(report.Versions.Driver)
            .Append("Loaded tooling: ").AppendLine(report.Versions.Tooling);
        if (report.Model is { } model)
            output.Append("Model fingerprint: ").AppendLine(model.Fingerprint)
                .Append("Mapped nodes: ").Append(model.Nodes.ToString(CultureInfo.InvariantCulture))
                .Append(" | relationships: ").AppendLine(model.Relationships.ToString(CultureInfo.InvariantCulture));
        foreach (var diagnosis in report.Diagnostics) Diagnosis(output, diagnosis);
        if (report.Database is { } database)
        {
            output.Append("Observed profile: ").Append(database.ProfileId).Append(" | Neo4j ").Append(database.ServerVersion)
                .Append(" / ").AppendLine(database.Edition)
                .Append("Schema comparison: ").AppendLine(database.SchemaStatus.ToString().ToLowerInvariant());
            foreach (var finding in database.Findings)
                output.Append("  ").Append(finding.Code).Append(" | ").Append(finding.Status).Append(" | count: ")
                    .AppendLine(finding.Count.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var query in report.Queries)
        {
            output.AppendLine().Append(query.ContractId).Append(" | compilation: ").Append(Status(query.Compilation))
                .Append(" | execution: ").AppendLine(Status(query.Execution));
            if (query.Definition is { } definition)
            {
                output.AppendLine(definition.Description).Append("Source: ").AppendLine(definition.SourceReference)
                    .Append("Declared comparison: ").Append(definition.Comparison.ToString().ToLowerInvariant())
                    .Append(" | duplicates: ").AppendLine(definition.Duplicates.ToString().ToLowerInvariant())
                    .Append("Declared budget (not execution evidence): ").Append(definition.Budget.MaxRows.ToString(CultureInfo.InvariantCulture))
                    .Append(" rows / ").Append(definition.Budget.Deadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)).AppendLine(" seconds");
                foreach (var column in definition.Columns)
                    output.Append("  Field: ").Append(column.Name).Append(" / ").Append(column.Kind)
                        .AppendLine(column.Nullable ? " / nullable" : " / required");
                if (definition.RequiredFeatures.Count != 0) output.Append("Declared features: ").AppendLine(string.Join(", ", definition.RequiredFeatures));
                if (definition.ReferenceId is not null) output.Append("Independent reference: ").AppendLine(definition.ReferenceId)
                    .Append("Required fixture: ").AppendLine(definition.FixtureId);
                if (definition.Budget.MaxPathHops is { } hops) output.Append("Declared maximum path hops: ").AppendLine(hops.ToString(CultureInfo.InvariantCulture));
            }
            if (query.StatementFingerprint is not null) output.Append("Statement fingerprint: ").AppendLine(query.StatementFingerprint);
            output.Append("Parameters: ").AppendLine(query.ParameterCount.ToString(CultureInfo.InvariantCulture));
            foreach (var parameter in query.Parameters)
                output.Append("  #").Append(parameter.Position.ToString(CultureInfo.InvariantCulture)).Append(": ").AppendLine(parameter.Kind);
            if (query.Diagnostic is not null) Diagnosis(output, query.Diagnostic);
            if (query.Statement is not null) output.AppendLine("Compiled Cypher (explicit disclosure):").AppendLine(query.Statement);
        }
        return output.ToString().ReplaceLineEndings("\n");
    }
    private static string Status(GraphEvidenceStatus status) => status == GraphEvidenceStatus.NotRun ? "not-run" : status.ToString().ToLowerInvariant();
    private static void Diagnosis(StringBuilder output, GraphDiagnosticResult diagnosis)
        => output.Append(diagnosis.Code).Append(diagnosis.Subcode is null ? "" : " (" + diagnosis.Subcode + ")").Append(" | ").Append(diagnosis.Phase).Append(" | ").AppendLine(Status(diagnosis.Status))
            .AppendLine(diagnosis.Explanation).Append("Action: ").AppendLine(diagnosis.SuggestedAction)
            .Append("Documentation: ").AppendLine(diagnosis.Documentation);
}
