namespace GraphBridge.Workshop;

/// <summary>Reads marked regions of the workshop's own source, embedded at build time.</summary>
public static class SourceRegions
{
    public static string Get(string file, string region)
    {
        var lines = Read(file).Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == "// workshop:start " + region);
        var end = Array.FindIndex(lines, l => l.Trim() == "// workshop:end " + region);
        if (start < 0 || end <= start) throw new InvalidOperationException($"Region '{region}' is not marked in {file}.");
        var body = lines[(start + 1)..end];
        var indent = body.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", body.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    public static string Read(string file)
    {
        var assembly = typeof(SourceRegions).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(n => n.Replace('\\', '/') == file)
            ?? throw new InvalidOperationException($"Source '{file}' is not embedded in the workshop assembly.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
