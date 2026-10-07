using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Services;

/// <summary>
/// When Fleet tries a turn a model provider's limit stopped again (<see cref="TurnRetryService"/>): when the provider
/// said, or after a wait that grows with each attempt, and not after <see cref="MaxAttempts"/> in a row.
/// </summary>
/// <remarks>
/// The harness has already retried a rate limit or an overload by itself before it gives up (Claude Code ten times,
/// OpenCode 2 about a minute and a half), so Fleet's first wait starts at a minute. A usage limit lasts until its
/// window resets: without a reset time, waiting a quarter of an hour at a time costs nothing, since a request the limit
/// turns away is free.
/// </remarks>
public static class TurnRetryPolicy
{
    /// <summary>How many times in a row Fleet tries a session again before it leaves it to the user.</summary>
    public const int MaxAttempts = 8;

    /// <summary>The shortest wait, however soon the provider said: the harness's own retries have just run out.</summary>
    public static readonly TimeSpan ShortestWait = TimeSpan.FromSeconds(15);

    /// <summary>After a usage limit's reset, which providers round (a "resets 3pm" can mean 3:00:40).</summary>
    public static readonly TimeSpan AfterReset = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] Waits =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    ];

    private static readonly TimeSpan[] UsageWaits =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60),
    ];

    /// <summary>
    /// When to send attempt <paramref name="attempt"/> (1 for the first) of a turn <paramref name="error"/> stopped, or
    /// <see langword="null"/> when Fleet doesn't try again: it isn't a limit, or the attempts are used up.
    /// </summary>
    public static (DateTimeOffset DueAt, bool ProviderSaid)? Plan(TurnError error, int attempt, DateTimeOffset now)
    {
        if (!TurnErrorKinds.IsKnown(error.Kind) || attempt < 1 || attempt > MaxAttempts)
            return null;

        if (error.RetryAt is { } said && said > now)
        {
            var after = error.Kind == TurnErrorKinds.UsageLimit ? AfterReset : TimeSpan.FromSeconds(1);
            var due = said + after;
            return (due < now + ShortestWait ? now + ShortestWait : due, true);
        }

        var waits = error.Kind == TurnErrorKinds.UsageLimit ? UsageWaits : Waits;
        return (now + waits[Math.Min(attempt, waits.Length) - 1], false);
    }
}
