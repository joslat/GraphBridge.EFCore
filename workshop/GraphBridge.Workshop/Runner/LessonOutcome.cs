namespace GraphBridge.Workshop;

public sealed record Checkpoint(string Name, bool Passed, string? Detail);

/// <summary>What a lesson observed. The runner prints it; tests assert it. Lessons contain no assertion code.</summary>
public sealed class LessonOutcome(string lessonId)
{
    private readonly List<Checkpoint> _checkpoints = [];
    private readonly List<KeyValuePair<string, string>> _cypher = [];

    public string LessonId { get; } = lessonId;
    public IReadOnlyList<Checkpoint> Checkpoints => _checkpoints;
    /// <summary>Generated Cypher by name, compared with reviewed snapshots.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Cypher => _cypher;
    /// <summary>The skip reason when a lesson needed a database that was not configured.</summary>
    public const string NeedsDatabase = "needs a Neo4j database";
    /// <summary>Why a lesson skipped some or all of its steps: no database, or a missing server capability.</summary>
    public string? Skipped { get; private set; }
    public bool Passed => _checkpoints.All(c => c.Passed);

    internal void Add(Checkpoint checkpoint) => _checkpoints.Add(checkpoint);
    internal void AddCypher(string name, string text)
    {
        if (_cypher.Any(c => c.Key == name)) throw new InvalidOperationException($"Duplicate Cypher snapshot name '{name}'.");
        _cypher.Add(new(name, text));
    }
    internal void Skip(string reason) => Skipped ??= reason;
}
