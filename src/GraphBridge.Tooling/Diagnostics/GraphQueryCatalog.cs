using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Tooling.Diagnostics;

public enum GraphQueryKind { FluentCypher }
public enum GraphResultComparison { Ordered, Multiset }
public enum GraphDuplicatePolicy { Preserve, Eliminate }
public enum GraphResultKind { String, Integer, FloatingPoint, Boolean }

/// <summary>A declared result field, not an inferred or independently verified result schema.</summary>
public sealed record GraphResultColumn
{
    public GraphResultColumn(string name, GraphResultKind kind, bool nullable = false)
    {
        EvidenceIdentity.Validate(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        (Name, Kind, Nullable) = (name, kind, nullable);
    }
    public string Name { get; }
    public GraphResultKind Kind { get; }
    public bool Nullable { get; }
}

/// <summary>Declared per-query execution limits. Compilation does not enforce a server deadline or execute a query.</summary>
public sealed record GraphQueryBudget
{
    public GraphQueryBudget(int maxRows, TimeSpan deadline, int? maxPathHops = null)
    {
        if (maxRows is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(deadline));
        if (maxPathHops is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(maxPathHops));
        (MaxRows, Deadline, MaxPathHops) = (maxRows, deadline, maxPathHops);
    }
    public int MaxRows { get; }
    public TimeSpan Deadline { get; }
    public int? MaxPathHops { get; }
}

/// <summary>Immutable application declarations shared by inspection, verification and presentation.</summary>
/// <remarks>Descriptions and source references are deliberately public metadata: never put secrets in them.
/// Requirements and expectations are declarations, not observed capability or semantic success.</remarks>
public sealed class GraphQueryDefinition
{
    public GraphQueryDefinition(string id, string description, string sourceReference,
        IEnumerable<GraphResultColumn> columns, GraphResultComparison comparison, GraphDuplicatePolicy duplicates,
        GraphQueryBudget budget, IEnumerable<string>? requiredFeatures = null, string? referenceId = null, string? fixtureId = null)
    {
        EvidenceIdentity.Validate(id);
        PublicText(description, nameof(description));
        PublicText(sourceReference, nameof(sourceReference));
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Enum.IsDefined(comparison)) throw new ArgumentOutOfRangeException(nameof(comparison));
        if (!Enum.IsDefined(duplicates)) throw new ArgumentOutOfRangeException(nameof(duplicates));
        var fields = columns.Take(65).ToArray();
        if (fields.Length is 0 or > 64 || fields.Any(c => c is null)
            || fields.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new ArgumentException("Declare 1..64 distinct, non-null output fields.", nameof(columns));
        var features = (requiredFeatures ?? []).Take(33).ToArray();
        if (features.Length > 32) throw new ArgumentException("At most 32 feature requirements are supported.", nameof(requiredFeatures));
        foreach (var feature in features) EvidenceIdentity.Validate(feature);
        if ((referenceId is null) != (fixtureId is null))
            throw new ArgumentException("An independent reference and fixture identifier must be declared together.");
        if (referenceId is not null) { EvidenceIdentity.Validate(referenceId); EvidenceIdentity.Validate(fixtureId!); }
        (Id, Description, SourceReference, Comparison, Duplicates, Budget, ReferenceId, FixtureId) =
            (id, description, sourceReference, comparison, duplicates, budget, referenceId, fixtureId);
        Columns = Array.AsReadOnly(fields);
        RequiredFeatures = Array.AsReadOnly(features.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
    public string Id { get; }
    public GraphQueryKind Kind => GraphQueryKind.FluentCypher;
    public string Description { get; }
    public string SourceReference { get; }
    public IReadOnlyList<GraphResultColumn> Columns { get; }
    public GraphResultComparison Comparison { get; }
    public GraphDuplicatePolicy Duplicates { get; }
    public GraphQueryBudget Budget { get; }
    public IReadOnlyList<string> RequiredFeatures { get; }
    public string? ReferenceId { get; }
    public string? FixtureId { get; }
    private static void PublicText(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || text.Any(char.IsControl))
            throw new ArgumentException("Public contract text must be 1..1024 characters without control characters.", name);
    }
}

/// <summary>A trusted, application-registered compiler factory. Owns no context, transport or model.</summary>
/// <remarks>Factories must only construct queries. They are application code, not sandboxed plugins;
/// do not register untrusted delegates or perform database operations inside a factory.</remarks>
public sealed class GraphQueryContract
{
    private readonly Func<DbContext, bool, CypherCommand> compile;
    private readonly Func<DbContext, bool, GraphPreparedContract>? prepare;
    private GraphQueryContract(GraphQueryDefinition definition, Func<DbContext, bool, CypherCommand> compile,
        Func<DbContext, bool, GraphPreparedContract>? prepare = null)
        => (Definition, this.compile, this.prepare) = (definition, compile, prepare);
    public GraphQueryDefinition Definition { get; }

    public static GraphQueryContract Fluent<TResult>(GraphQueryDefinition definition,
        Func<DbContext, CypherResultQuery<TResult>> factory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(factory);
        return new(definition, (db, snapshot) =>
        {
            var query = factory(db) ?? throw new InvalidOperationException("The registered query factory returned null.");
            return snapshot ? query.Inspect() : query.ToCommand();
        });
    }

    /// <summary>Registers a typed scalar-row projection for explicit semantic verification.</summary>
    /// <remarks>The trusted projection runs after the original compiled reader. It must preserve column order,
    /// nulls and duplicates; it must not perform I/O. Compilation/inspection alone never invokes it.</remarks>
    public static GraphQueryContract Fluent<TResult>(GraphQueryDefinition definition,
        Func<DbContext, CypherResultQuery<TResult>> factory, Func<TResult, IReadOnlyList<object?>> rowProjection)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(rowProjection);
        CypherResultQuery<TResult> Query(DbContext db) => factory(db)
            ?? throw new InvalidOperationException("The registered query factory returned null.");
        return new(definition, (db, snapshot) => snapshot ? Query(db).Inspect() : Query(db).ToCommand(), (db, snapshot) =>
        {
            var prepared = Query(db).Prepare(snapshot);
            if (prepared.MaxRows > definition.Budget.MaxRows)
                throw new InvalidOperationException("The query limit exceeds the declared verification row budget.");
            return new(prepared.Command, async cancellationToken =>
            {
                var values = await prepared.ToListAsync(cancellationToken).ConfigureAwait(false);
                var rows = new IReadOnlyList<object?>[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = rowProjection(values[i]) ?? throw new InvalidOperationException("A result projection returned null.");
                    if (row.Count != definition.Columns.Count)
                        throw new InvalidOperationException("A result projection has the wrong column count.");
                    rows[i] = Array.AsReadOnly(row.ToArray());
                }
                return Array.AsReadOnly(rows);
            });
        });
    }

    internal GraphPreparedContract Prepare(DbContext context, bool captureSemantics = false) => prepare?.Invoke(context, captureSemantics)
        ?? throw new NotSupportedException("Semantic verification requires a scalar-row projection registration.");

    /// <summary>Invokes the registered factory and the existing compiler. Parameters remain sensitive application data.</summary>
    public CypherCommand Compile(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return compile(context, false);
    }

    internal CypherCommand Inspect(DbContext context) => compile(context, true);
}

// Execution delegates and sensitive command parameters stay outside serialized evidence.
internal sealed record GraphPreparedContract(CypherCommand Command,
    Func<CancellationToken, Task<IReadOnlyList<IReadOnlyList<object?>>>> ExecuteAsync);

/// <summary>An immutable, deterministically ordered set of trusted application registrations.</summary>
public sealed class GraphQueryCatalog
{
    /// <summary>A catalog without search index declarations; kept for binary compatibility with 0.1.0-preview.3.</summary>
    public GraphQueryCatalog(IEnumerable<GraphQueryContract> contracts) : this(contracts, null) { }

    /// <param name="searchIndexes">Search indexes the application depends on; connected Doctor checks them. Offline inspection only lists them.</param>
    public GraphQueryCatalog(IEnumerable<GraphQueryContract> contracts, IEnumerable<GraphSearchIndexExpectation>? searchIndexes)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var snapshot = contracts.Take(257).ToArray();
        if (snapshot.Length > 256 || snapshot.Any(c => c is null)
            || snapshot.Select(c => c.Definition.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Catalogs allow at most 256 non-null contracts with distinct IDs.", nameof(contracts));
        var indexes = (searchIndexes ?? []).Take(65).ToArray();
        if (indexes.Length > 64 || indexes.Any(i => i is null)
            || indexes.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != indexes.Length)
            throw new ArgumentException("Catalogs allow at most 64 non-null search index declarations with distinct IDs.", nameof(searchIndexes));
        Contracts = Array.AsReadOnly(snapshot.OrderBy(c => c.Definition.Id, StringComparer.Ordinal).ToArray());
        SearchIndexes = Array.AsReadOnly(indexes.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray());
    }
    public IReadOnlyList<GraphQueryContract> Contracts { get; }
    public IReadOnlyList<GraphSearchIndexExpectation> SearchIndexes { get; }
}
