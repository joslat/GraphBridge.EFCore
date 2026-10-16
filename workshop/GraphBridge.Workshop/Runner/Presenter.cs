using System.Collections;
using System.Reflection;

namespace GraphBridge.Workshop;

/// <summary>Console presentation for lessons. Color is used only for an interactive terminal.</summary>
public sealed class Presenter(TextWriter output, bool color)
{
    private const int MaxRows = 20;
    public TextWriter Output { get; } = output;

    public static Presenter ForConsole() => new(Console.Out, !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null);

    public void LessonHeader(LessonInfo info, bool connected)
    {
        var mode = info.Mode switch { LessonMode.Offline => "offline", LessonMode.Connected => "needs Neo4j", _ => "offline + Neo4j" };
        Output.WriteLine();
        Output.WriteLine(Paint(new string('═', 72), Ansi.Dim));
        Output.WriteLine(Paint($" {info.Id} · {info.Title}", Ansi.Bold) + Paint($"   {(info.Core ? "★ core · " : "")}{mode}", Ansi.Dim));
        Output.WriteLine(Paint(new string('═', 72), Ansi.Dim));
        Output.WriteLine($" Goal: {info.Goal}");
        Output.WriteLine(Paint($" Script: workshop/GraphBridge.Workshop/Lessons/{info.Folder}/README.md" + (connected ? "" : "  (no database configured)"), Ansi.Dim));
    }

    public void Step(int number, string text)
    {
        Output.WriteLine();
        Output.WriteLine(Paint($"▸ Step {number} · {text}", Ansi.Cyan));
    }

    public void Code(string code)
    {
        foreach (var line in code.Split('\n')) Output.WriteLine(Paint("  │ ", Ansi.Dim) + line);
    }

    public void Cypher(string text)
    {
        Output.WriteLine(Paint("  Cypher", Ansi.Magenta));
        foreach (var line in text.Replace("\r", "").Split('\n')) Output.WriteLine(Paint("  ⟩ ", Ansi.Magenta) + line);
    }

    public void Value(string label, object? value) => Output.WriteLine($"  {label}: {Format(value)}");
    public void Note(string text) => Output.WriteLine(Paint("  ℹ ", Ansi.Blue) + text);
    public void Limit(string text) => Output.WriteLine(Paint("  ⚠ Limit: ", Ansi.Yellow) + text);
    public void Warning(string text) => Output.WriteLine(Paint("  ⚠ ", Ansi.Yellow) + text);

    /// <summary>Prints public properties of each row (records and anonymous types) as an aligned table.</summary>
    public void Table<T>(IEnumerable<T> rows)
    {
        var all = rows.ToList();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var simple = properties.Length == 0 || typeof(T) == typeof(string) || typeof(T).IsPrimitive;
        var headers = simple ? ["value"] : properties.Select(p => p.Name).ToArray();
        var cells = all.Take(MaxRows).Select(row => simple ? [Format(row)] : properties.Select(p => Format(p.GetValue(row))).ToArray()).ToList();
        var widths = headers.Select((h, i) => Math.Min(40, Math.Max(h.Length, cells.Select(c => c[i].Length).DefaultIfEmpty(0).Max()))).ToArray();
        Output.WriteLine("  " + string.Join("  ", headers.Select((h, i) => Paint(h.PadRight(widths[i]), Ansi.Bold))));
        foreach (var row in cells)
            Output.WriteLine("  " + string.Join("  ", row.Select((c, i) => Fit(c, widths[i]))));
        if (all.Count == 0) Output.WriteLine(Paint("  (no rows)", Ansi.Dim));
        if (all.Count > MaxRows) Output.WriteLine(Paint($"  … {all.Count - MaxRows} more rows", Ansi.Dim));
    }

    public void Checkpoint(Checkpoint checkpoint)
        => Output.WriteLine(checkpoint.Passed
            ? Paint("  ✔ ", Ansi.Green) + checkpoint.Name
            : Paint("  ✘ ", Ansi.Red) + checkpoint.Name + (checkpoint.Detail is null ? "" : Paint($" — {checkpoint.Detail}", Ansi.Red)));

    public void Summary(LessonOutcome outcome)
    {
        Output.WriteLine();
        var passed = outcome.Checkpoints.Count(c => c.Passed);
        if (outcome.Skipped is not null && outcome.Checkpoints.Count == 0)
            Output.WriteLine(Paint($" ◌ Lesson {outcome.LessonId} skipped: {outcome.Skipped}", Ansi.Yellow));
        else
            Output.WriteLine(outcome.Passed
                ? Paint($" ✔ Lesson {outcome.LessonId}: {passed}/{outcome.Checkpoints.Count} checkpoints passed", Ansi.Green)
                  + (outcome.Skipped is null ? "" : Paint($" (database steps skipped: {outcome.Skipped})", Ansi.Yellow))
                : Paint($" ✘ Lesson {outcome.LessonId}: {passed}/{outcome.Checkpoints.Count} checkpoints passed", Ansi.Red));
    }

    public string Paint(string text, string code) => color ? code + text + Ansi.Reset : text;

    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm zzz"),
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Format)) + "]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

    private static string Fit(string text, int width) => text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";

    private static class Ansi
    {
        public const string Reset = "\u001b[0m", Bold = "\u001b[1m", Dim = "\u001b[2m", Red = "\u001b[31m", Green = "\u001b[32m",
            Yellow = "\u001b[33m", Blue = "\u001b[34m", Magenta = "\u001b[35m", Cyan = "\u001b[36m";
    }
}
