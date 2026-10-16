using GraphBridge.Workshop.Domain;
using Neo4j.Driver;

namespace GraphBridge.Workshop;

/// <summary>Everything a lesson needs: contexts, presentation and checkpoints.</summary>
public sealed class LessonContext
{
    private readonly WorkshopConnection? _connection;
    private readonly Presenter _presenter;
    private int _step;

    internal LessonContext(LessonInfo info, WorkshopConnection? connection, Presenter presenter, LessonOutcome outcome)
        => (Info, _connection, _presenter, Outcome) = (info, connection, presenter, outcome);

    public LessonInfo Info { get; }
    public LessonOutcome Outcome { get; }
    public bool IsConnected => _connection is not null;
    /// <summary>The application-owned driver. Offline, it is a stand-in that refuses any database access.</summary>
    public IDriver Driver => _connection?.Driver ?? OfflineDriver.Instance;
    public string Database => _connection?.Database ?? "neo4j";
    public WorkshopConnection? Connection => _connection;

    /// <summary>A new context; dispose it with await using. Offline contexts compile queries but never connect.</summary>
    public MoviesContext CreateContext() => new(Driver, Database);

    public void Step(string text) => _presenter.Step(++_step, text);

    /// <summary>Prints a region of real source, marked by // workshop:start Name and // workshop:end Name.</summary>
    public void Code(string region, string? file = null)
        => _presenter.Code(SourceRegions.Get(file ?? $"Lessons/{Info.Folder}/Lesson.cs", region));

    /// <summary>Prints generated Cypher and records it for comparison with the reviewed snapshot.</summary>
    public void Cypher(string name, string text)
    {
        Outcome.AddCypher(name, text);
        _presenter.Cypher(text);
    }

    public void Value(string label, object? value) => _presenter.Value(label, value);
    public void Table<T>(IEnumerable<T> rows) => _presenter.Table(rows);
    public void Note(string text) => _presenter.Note(text);
    public void Limit(string text) => _presenter.Limit(text);

    public void Check(string name, bool passed, string? detail = null)
    {
        var checkpoint = new Checkpoint(name, passed, passed ? null : detail);
        Outcome.Add(checkpoint);
        _presenter.Checkpoint(checkpoint);
    }

    public void Expect<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected {Presenter.Format(expected)}, got {Presenter.Format(actual)}");

    public void ExpectSequence<T>(string name, IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var (e, a) = (expected.ToList(), actual.ToList());
        Check(name, e.SequenceEqual(a), $"expected {Presenter.Format(e)}, got {Presenter.Format(a)}");
    }

    /// <summary>Records an expected rejection: the action must throw TException.</summary>
    public TException? ExpectFailure<TException>(string name, Action action) where TException : Exception
    {
        try
        {
            action();
            Check(name, false, $"no {typeof(TException).Name} was thrown");
            return null;
        }
        catch (TException error)
        {
            Check(name, true);
            _presenter.Note($"{typeof(TException).Name}: {error.Message}");
            return error;
        }
    }

    /// <summary>Records an expected rejection from an asynchronous operation.</summary>
    public async Task<TException?> ExpectFailureAsync<TException>(string name, Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            Check(name, false, $"no {typeof(TException).Name} was thrown");
            return null;
        }
        catch (TException error)
        {
            Check(name, true);
            _presenter.Note($"{typeof(TException).Name}: {error.Message}");
            return error;
        }
    }

    /// <summary>Skips the remaining steps for a stated reason, such as a server without a required capability.</summary>
    public void Skip(string reason)
    {
        Outcome.Skip(reason);
        _presenter.Note("Skipping: " + reason);
    }

    /// <summary>False when no database is configured; the remaining steps are skipped and the reason is shown.</summary>
    public bool NeedsDatabase(string what)
    {
        if (IsConnected) return true;
        Outcome.Skip(LessonOutcome.NeedsDatabase);
        _presenter.Note($"Skipping: {what} needs Neo4j. Set GRAPHBRIDGE_WORKSHOP_URI and GRAPHBRIDGE_WORKSHOP_PASSWORD (see the workshop README), then run this lesson again.");
        return false;
    }
}
