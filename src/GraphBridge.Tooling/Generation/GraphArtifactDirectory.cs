using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphBridge.Tooling.Generation;

/// <summary>Publishes a complete generated set; refuses to overwrite unrelated or manually changed files.</summary>
public static class GraphArtifactDirectory
{
    private const string Marker = ".graphbridge-generated.json";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool Matches(GraphArtifactResult result, string directory)
    {
        var expected = Files(result);
        var destination = Destination(directory);
        if (!Directory.Exists(destination)) return false;
        EnsureNoLinks(destination);
        var actual = Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        return actual.SequenceEqual(expected.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            && expected.All(file => File.Exists(Path.Combine(destination, file.Key))
                && !IsLink(Path.Combine(destination, file.Key))
                && File.ReadAllBytes(Path.Combine(destination, file.Key)).AsSpan().SequenceEqual(Utf8.GetBytes(file.Value)));
    }

    public static void Publish(GraphArtifactResult result, string directory)
    {
        var files = Files(result); // Validate the complete result before filesystem changes.
        var destination = Destination(directory);
        EnsureNoLinks(destination);
        if (Directory.Exists(destination)) ValidateOwnedDirectory(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".graphbridge-stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".graphbridge-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var movedOld = false;
        var published = false;
        try
        {
            foreach (var file in files) File.WriteAllText(Path.Combine(stage, file.Key), file.Value, Utf8);
            // Recheck immediately before moving. Neither source nor destination may be a link.
            EnsureNoLinks(destination);
            if (Directory.Exists(destination))
            {
                ValidateOwnedDirectory(destination);
                Directory.Move(destination, backup);
                movedOld = true;
            }
            Directory.Move(stage, destination);
            published = true;
        }
        catch
        {
            if (movedOld && !Directory.Exists(destination)) Directory.Move(backup, destination);
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) DeleteOwnedFiles(stage, files.Keys);
        }
        if (published && movedOld)
        {
            // Only a verified generated directory was moved here. No recursive deletion is used.
            ValidateOwnedDirectory(backup);
            DeleteOwnedFiles(backup, Directory.EnumerateFiles(backup).Select(path => Path.GetFileName(path)!).ToArray());
        }
    }

    private static SortedDictionary<string, string> Files(GraphArtifactResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success || result.Files.Count == 0) throw new InvalidOperationException("Failed or empty generation cannot be published.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in result.Files)
        {
            if (!ValidName(file.RelativePath) || file.RelativePath == Marker || !files.TryAdd(file.RelativePath, file.Content))
                throw new InvalidOperationException("Generated artifact names must be unique, fixed leaf filenames.");
        }
        var hashes = files.Select(file => new ArtifactHash(file.Key, Hash(Utf8.GetBytes(file.Value)))).ToArray();
        files.Add(Marker, JsonSerializer.Serialize(new ArtifactSet(1, hashes), JsonOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        return files;
    }

    private static string Destination(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!) || File.Exists(full))
            throw new ArgumentException("Output must be a dedicated generated directory.", nameof(directory));
        return full;
    }

    private static void ValidateOwnedDirectory(string directory)
    {
        EnsureNoLinks(directory);
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Length == 0) return;
        if (entries.Any(path => IsLink(path) || Directory.Exists(path)))
            throw new IOException("Output contains links/subdirectories and cannot be replaced.");
        var marker = Path.Combine(directory, Marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 1_048_576)
            throw new IOException("Output is not a recognized generated artifact set; existing files were preserved.");
        ArtifactSet prior;
        try { prior = JsonSerializer.Deserialize<ArtifactSet>(File.ReadAllText(marker, Utf8)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("Invalid generated artifact marker; existing files were preserved.", error); }
        if (prior.FormatVersion != 1 || prior.Files is null || prior.Files.Any(f => f is null || !ValidName(f.Path) || f.Path == Marker)
            || prior.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != prior.Files.Length
            || !entries.Select(Path.GetFileName).Order(StringComparer.Ordinal)
                .SequenceEqual(prior.Files.Select(f => f.Path).Append(Marker).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new IOException("Output contains unrecognized files; existing files were preserved.");
        foreach (var file in prior.Files)
            if (Hash(File.ReadAllBytes(Path.Combine(directory, file.Path))) != file.Sha256)
                throw new IOException("Generated artifact was manually changed: " + file.Path + ". Existing files were preserved.");
    }

    private static void DeleteOwnedFiles(string directory, IEnumerable<string> names)
    {
        EnsureNoLinks(directory);
        foreach (var name in names)
        {
            if (!ValidName(name)) throw new IOException("Invalid generated cleanup filename.");
            var path = Path.Combine(directory, name);
            if (IsLink(path)) throw new IOException("Generated cleanup refuses linked files.");
            File.Delete(path);
        }
        Directory.Delete(directory); // Fails rather than removing any unexpected content.
    }

    private static void EnsureNoLinks(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Generated output paths cannot traverse filesystem links.");
    }
    private static bool IsLink(string path) => File.Exists(path) || Directory.Exists(path)
        ? (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 : false;
    private static bool ValidName(string name) => !string.IsNullOrEmpty(name) && name is not ("." or "..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record ArtifactHash(string Path, string Sha256);
    private sealed record ArtifactSet(int FormatVersion, ArtifactHash[] Files);
}
