namespace GraphBridge.Workshop;

/// <summary>Whether a lesson needs a database. Mixed lessons run their offline part without one.</summary>
public enum LessonMode { Offline, Connected, Mixed }

/// <summary>What the runner prepares before a connected lesson runs.</summary>
public enum Requirement
{
    /// <summary>Nothing: the runner prepares nothing (lesson 00 runs the guard itself).</summary>
    None,
    /// <summary>Safety guard only; the lesson manages schema itself (lesson 03).</summary>
    Guarded,
    /// <summary>Guard plus the reviewed uniqueness constraints.</summary>
    Schema,
    /// <summary>Guard, constraints and the deterministic movie graph.</summary>
    Seed
}

/// <param name="Id">Two-digit lesson number; also the folder prefix.</param>
/// <param name="Part">Workshop part letter: A foundations … F developer extensions.</param>
/// <param name="Folder">Folder under Lessons/, used to find the lesson's embedded source.</param>
/// <param name="Goal">One sentence shown before the lesson starts.</param>
public sealed record LessonInfo(string Id, char Part, string Title, string Folder, LessonMode Mode, bool Core,
    Requirement Requires, string Goal);

/// <summary>One workshop lesson: a single topic, its script in README.md and code that prints what it does.</summary>
public interface ILesson
{
    LessonInfo Info { get; }
    Task RunAsync(LessonContext lesson, CancellationToken cancellationToken);
}
