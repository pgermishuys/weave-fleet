namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// How much of its account's usage limits a harness has used: for Claude Code on a claude.ai subscription, the 5-hour
/// and weekly windows. The payload of <see cref="EventTypes.HarnessUsage"/>. It's the account's, not one session's, so
/// Fleet keeps the latest per harness and window. Harnesses that don't report limits (API keys, gateways) never send it.
/// </summary>
public sealed record UsageLimitReport
{
    /// <summary>The windows the harness reported this time; others keep what was said before.</summary>
    public IReadOnlyList<UsageLimitWindow> Windows { get; init; } = [];
}

/// <summary>One usage-limit window: how much of it is used, and when it starts again.</summary>
public sealed record UsageLimitWindow
{
    /// <summary>Which window: one of <see cref="UsageLimitWindows"/>, or the harness's own name for another.</summary>
    public required string Window { get; init; }

    /// <summary>How much of it is used, from 0 to 1, when the harness says.</summary>
    public double? Utilization { get; init; }

    /// <summary>When it resets, when the harness says.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>One of <see cref="UsageLimitStatuses"/>.</summary>
    public string Status { get; init; } = UsageLimitStatuses.Allowed;
}

/// <summary>The windows harnesses report (<see cref="UsageLimitWindow.Window"/>), under Claude Code's names.</summary>
public static class UsageLimitWindows
{
    public const string FiveHour = "five_hour";
    public const string SevenDay = "seven_day";
    public const string SevenDayOpus = "seven_day_opus";
    public const string SevenDaySonnet = "seven_day_sonnet";
}

/// <summary>Where a window stands: <see cref="UsageLimitWindow.Status"/>.</summary>
public static class UsageLimitStatuses
{
    /// <summary>In use, with room left.</summary>
    public const string Allowed = "allowed";

    /// <summary>Getting close: the harness warned about it.</summary>
    public const string Warning = "warning";

    /// <summary>Used up: turns stop until it resets.</summary>
    public const string Rejected = "rejected";
}
