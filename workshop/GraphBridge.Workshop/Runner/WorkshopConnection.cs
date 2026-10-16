using System.Net;
using Neo4j.Driver;

namespace GraphBridge.Workshop;

/// <summary>Command-line switches that widen what the workshop may touch. Each must be given explicitly.</summary>
public sealed record WorkshopOptions(bool AllowRemote = false, bool AllowOtherData = false);

/// <summary>The application-owned Neo4j driver for the workshop database.</summary>
/// <remarks>Settings come from environment variables only; credentials are never command arguments or files in the
/// repository. GRAPHBRIDGE_WORKSHOP_* takes precedence; the repository fixture's GRAPHBRIDGE_NEO4J_* is a fallback.</remarks>
public sealed class WorkshopConnection : IAsyncDisposable
{
    private readonly bool _ownsDriver;

    private WorkshopConnection(IDriver driver, Uri uri, string database, WorkshopOptions options, bool ownsDriver)
        => (Driver, Uri, Database, Options, _ownsDriver) = (driver, uri, database, options, ownsDriver);

    public IDriver Driver { get; }
    public Uri Uri { get; }
    public string Database { get; }
    public WorkshopOptions Options { get; }

    /// <summary>Null when no database is configured: lessons then run offline.</summary>
    public static WorkshopConnection? FromEnvironment(WorkshopOptions options)
    {
        // All settings come from one prefix, so a fixture password is never sent to a different workshop URI.
        var prefix = Environment.GetEnvironmentVariable("GRAPHBRIDGE_WORKSHOP_URI") is { Length: > 0 } ? "GRAPHBRIDGE_WORKSHOP_"
            : Environment.GetEnvironmentVariable("GRAPHBRIDGE_NEO4J_URI") is { Length: > 0 } ? "GRAPHBRIDGE_NEO4J_" : null;
        if (prefix is null) return null;
        string? Setting(string name) => Environment.GetEnvironmentVariable(prefix + name) is { Length: > 0 } value ? value : null;
        var uriText = Setting("URI")!;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme is not ("bolt" or "neo4j" or "bolt+s" or "neo4j+s" or "bolt+ssc" or "neo4j+ssc"))
            throw new WorkshopConfigurationException($"{prefix}URI is not a bolt:// or neo4j:// URI.");
        // The URI is printed in headers and messages, so it must not carry credentials.
        if (uri.UserInfo.Length > 0)
            throw new WorkshopConfigurationException($"{prefix}URI contains a user name or password. Put them in {prefix}USERNAME and {prefix}PASSWORD instead.");
        if (!options.AllowRemote && !IsLoopback(uri))
            throw new WorkshopConfigurationException($"{uri.Host} is not this machine. The workshop writes and resets data, so it only uses a local database unless you pass --allow-remote for a disposable remote one.");
        var password = Setting("PASSWORD")
            ?? throw new WorkshopConfigurationException($"Set {prefix}PASSWORD (and {prefix}USERNAME if it is not neo4j).");
        var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(Setting("USERNAME") ?? "neo4j", password),
            // A local workshop database answers at once or not at all: retry briefly, then explain.
            builder => builder.WithConnectionTimeout(TimeSpan.FromSeconds(10)).WithMaxTransactionRetryTime(TimeSpan.FromSeconds(5)));
        return new(driver, uri, Setting("DATABASE") ?? "neo4j", options, ownsDriver: true);
    }

    /// <summary>For tests and hosts that already own a driver for a disposable database; the caller keeps ownership.</summary>
    public static WorkshopConnection ForDriver(IDriver driver, Uri uri, string database, WorkshopOptions options)
    {
        if (!options.AllowRemote && !IsLoopback(uri))
            throw new WorkshopConfigurationException($"{uri.Host} is not this machine; pass --allow-remote for a disposable remote database.");
        return new(driver, uri, database, options, ownsDriver: false);
    }

    public static bool IsLoopback(Uri uri)
        => uri.Host == "localhost" || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    public ValueTask DisposeAsync() => _ownsDriver ? Driver.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>Configuration or safety refusal. The workshop exits with code 2 and changes nothing.</summary>
public class WorkshopConfigurationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The configured database cannot be used at all: not reachable, wrong credentials or no such database.</summary>
public sealed class WorkshopConnectionException(string message, Exception inner) : WorkshopConfigurationException(message, inner);
