using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>
/// Doctor's check of additional labels (ADR 0055). A node that carries a type's own label but not all of its additional labels
/// is outside the type: queries of the type skip it, keyed writes don't touch it, and a read that reaches it through a native
/// reference or an Include fails. Read only, in one rolled-back transaction with a 60-second timeout. When every node is
/// labelled, it reads every node of each such label; the count stops at <c>maxRows</c>. The warning names no label, as
/// Doctor's reports never do.
/// </summary>
internal static class GraphLabelInspection
{
    internal static bool Applies(GraphSchemaManifest manifest) => manifest.Nodes.Any(n => n.AdditionalLabels.Count > 0);

    internal static async Task<IReadOnlyList<GraphDiagnosticResult>> InspectAsync(DbContext context, GraphSchemaManifest manifest, int maxRows,
        CancellationToken cancellationToken)
    {
        var options = context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("Label checks require a GraphBridge context configured with UseNeo4j.");
        cancellationToken.ThrowIfCancellationRequested();
        IAsyncSession? session = null;
        IAsyncTransaction? transaction = null;
        try
        {
            session = options.Driver.AsyncSession(builder => builder.WithDatabase(options.DatabaseName).WithDefaultAccessMode(AccessMode.Read));
            transaction = await session.BeginTransactionAsync(builder => builder.WithTimeout(TimeSpan.FromSeconds(60))
                .WithMetadata(new Dictionary<string, object> { ["graphbridgeOperation"] = "doctor-labels" })).ConfigureAwait(false);
            foreach (var node in manifest.Nodes.Where(n => n.AdditionalLabels.Count > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The manifest's labels come from the validated model; they are quoted all the same.
                var all = string.Join(" AND ", node.AdditionalLabels.Select(label => "n:" + Quote(label)));
                var cursor = await transaction.RunAsync($"MATCH (n:{Quote(node.Label)}) WHERE NOT ({all}) WITH n LIMIT $limit RETURN count(n) AS missing",
                    new Dictionary<string, object> { ["limit"] = (long)maxRows }).ConfigureAwait(false);
                var missing = (await cursor.SingleAsync().ConfigureAwait(false))["missing"].As<long>();
                if (missing > 0) return [GraphDiagnosticCatalog.Get("GBD-LABEL-001")];
            }
            return [GraphDiagnosticCatalog.Get("GBD-LABEL-000")];
        }
        finally
        {
            // Observation only: never commit.
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync().ConfigureAwait(false); }
                catch { /* Preserve the primary outcome; disposal follows. */ }
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Quote(string label)
    {
        if (label.Length == 0 || !(char.IsAsciiLetter(label[0]) || label[0] == '_') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidOperationException("A manifest label is not a valid graph identifier.");
        return "`" + label + "`";
    }
}
