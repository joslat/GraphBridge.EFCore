using System.Net;

namespace GraphBridge.GraphRag;

/// <summary>
/// How the toolkit retries a failed model call while it ingests, extracts or re-embeds (ADR 0052, section 11): which failures,
/// how often, and how long to wait. A server's retry hint, such as a 429's <c>Retry-After</c>, is honoured; a hint longer than
/// <see cref="MaxDelay"/> fails the call at once instead of waiting. Clients built on <c>System.ClientModel</c> (OpenAI, Azure
/// OpenAI) retry on their own and throw their own exception type, which the default <see cref="IsTransient"/> doesn't retry.
/// </summary>
public sealed class GraphRagRetryOptions
{
    /// <summary>The key under which a client puts a <see cref="TimeSpan"/> retry hint in an exception's <see cref="Exception.Data"/>.</summary>
    public const string RetryAfterKey = "GraphBridge.RetryAfter";

    /// <summary>0 to 10 retries after the first attempt; 0 leaves retrying to the client.</summary>
    public int MaxRetries { get; set; } = 4;
    /// <summary>The first wait without a hint, 1 ms to 1 minute; it doubles with each retry, with up to 25% jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>The longest single wait, 1 ms to 10 minutes. A longer hint fails the call instead.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>Which failures are retried. Null: an <see cref="HttpRequestException"/> with status 408, 429 or 5xx, a dropped
    /// connection, or a timeout.</summary>
    public Func<Exception, bool>? IsTransient { get; set; }
    /// <summary>How long a failure asks to wait. Null: a <see cref="TimeSpan"/> stored under <see cref="RetryAfterKey"/> in the
    /// exception's data or its inner exception's.</summary>
    public Func<Exception, TimeSpan?>? RetryAfter { get; set; }

    internal void Validate()
    {
        if (MaxRetries is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(MaxRetries), "MaxRetries must be 0 to 10.");
        if (BaseDelay < TimeSpan.FromMilliseconds(1) || BaseDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), "BaseDelay must be 1 ms to 1 minute.");
        if (MaxDelay < TimeSpan.FromMilliseconds(1) || MaxDelay > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), "MaxDelay must be 1 ms to 10 minutes.");
    }
}

/// <summary>Retries in one ingestion or re-embedding call: how many, how many answered a rate limit, and the time spent waiting.</summary>
public sealed record GraphRagRetrySummary(int Retries, int RateLimited, TimeSpan Waited);

/// <summary>Counts retries across parallel lanes.</summary>
internal sealed class GraphRagRetryTally
{
    private int _retries, _rateLimited;
    private long _waitedTicks;

    public void Add(bool rateLimited, TimeSpan wait)
    {
        Interlocked.Increment(ref _retries);
        if (rateLimited) Interlocked.Increment(ref _rateLimited);
        Interlocked.Add(ref _waitedTicks, wait.Ticks);
    }

    public GraphRagRetrySummary Summary() => new(Volatile.Read(ref _retries), Volatile.Read(ref _rateLimited), TimeSpan.FromTicks(Interlocked.Read(ref _waitedTicks)));
}

internal static class GraphRagRetry
{
    /// <summary>
    /// Runs a model call with the retry policy. A failure that isn't retried, or retries that run out, throws what
    /// <paramref name="fail"/> makes of <paramref name="failureCode"/>, or of <c>rate-limited</c> when the last failure was a 429,
    /// with the call's own exception inside it and recorded on the current activity: the code alone wouldn't say why, such as a
    /// 401 from a wrong key. Cancellation of <paramref name="cancellationToken"/> is never retried.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, GraphRagRetryOptions options, GraphRagRetryTally tally,
        TimeProvider clock, string failureCode, Func<string, Exception, Exception> fail, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var limited = IsRateLimit(error);
                var code = limited ? "rate-limited" : failureCode;
                var retry = attempt < options.MaxRetries && (options.IsTransient ?? IsTransient)(error);
                var hint = retry ? (options.RetryAfter ?? RetryAfter)(error) : null;
                if (!retry || hint > options.MaxDelay)
                {
                    System.Diagnostics.Activity.Current?.AddException(error);
                    throw fail(code, error);
                }
                var wait = hint is { } asked && asked > TimeSpan.Zero ? asked : Backoff(options, attempt);
                tally.Add(limited, wait);
                await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static TimeSpan Backoff(GraphRagRetryOptions options, int attempt)
    {
        var doubled = options.BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20));
        var jittered = doubled * (1 + Random.Shared.NextDouble() * 0.25);
        return TimeSpan.FromMilliseconds(Math.Min(jittered, options.MaxDelay.TotalMilliseconds));
    }

    internal static bool IsTransient(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded } => true,
        TimeoutException or TaskCanceledException => true,
        _ => false
    };

    private static bool IsRateLimit(Exception error) => error is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };

    private static TimeSpan? RetryAfter(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current.Data[GraphRagRetryOptions.RetryAfterKey] is TimeSpan hint) return hint;
        return null;
    }
}
