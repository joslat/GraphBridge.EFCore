using System.Diagnostics.Tracing;

namespace GraphBridge.Benchmarks;

public static class AllocationProfileMode
{
    public static string? Extract(ref string[] arguments)
    {
        const string prefix = "--allocation-backend=";
        var choices = arguments.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (choices.Length == 0) return null;
        if (choices.Length != 1 || choices[0][prefix.Length..] is not ("driver" or "ef"))
            throw new ArgumentException("Select exactly one allocation backend: driver or ef.");
        arguments = arguments.Where(a => !a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return choices[0][prefix.Length..];
    }
}

[EventSource(Name = "GraphBridge-AllocationProfile")]
public sealed class AllocationProfileEvents : EventSource
{
    public static readonly AllocationProfileEvents Log = new();
    private AllocationProfileEvents() { }

    [Event(1, Level = EventLevel.Informational)]
    public void SampleBegin(string backend, string phase, int iteration)
    {
        if (IsEnabled()) WriteEvent(1, backend, phase, iteration);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void SampleEnd(string backend, string phase, int iteration)
    {
        if (IsEnabled()) WriteEvent(2, backend, phase, iteration);
    }
}
