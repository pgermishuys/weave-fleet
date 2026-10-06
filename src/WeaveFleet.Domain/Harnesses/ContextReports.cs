namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// What a harness says about its session's context window: the size of the last model call and the model's limits.
/// The payload of <see cref="EventTypes.ContextUsage"/>, which every adapter sends after each model call of the
/// session's own (not a subagent's), and when it learns the model's limits.
/// </summary>
public sealed record ContextUsageReport
{
    /// <summary>The tokens of the last model call; null when the report only brings the model's limits.</summary>
    public ContextCall? Call { get; init; }

    /// <summary>The model's context window in tokens, when the harness knows it.</summary>
    public int? Limit { get; init; }

    /// <summary>How many tokens in the context make the harness compact on its own, when it can tell.</summary>
    public int? CompactsAt { get; init; }

    /// <summary>The model the call ran on (or the limits are for).</summary>
    public string? ModelId { get; init; }

    public string? ProviderId { get; init; }
}

/// <summary>
/// One model call's tokens, each counted once: <see cref="Input"/> without the cached tokens and
/// <see cref="Output"/> without the reasoning, the way every harness Fleet drives reports them.
/// </summary>
public sealed record ContextCall
{
    /// <summary>Input tokens that weren't read from or written to the provider's cache.</summary>
    public int Input { get; init; }

    public int CacheRead { get; init; }

    public int CacheWrite { get; init; }

    /// <summary>Output tokens, without <see cref="Reasoning"/>.</summary>
    public int Output { get; init; }

    public int Reasoning { get; init; }

    /// <summary>
    /// How much of the context window the call filled: everything it read plus everything it wrote, which the next
    /// call starts from. The figure OpenCode, OpenCode 2 and Pi show for their own context.
    /// </summary>
    public int Used => Input + CacheRead + CacheWrite + Output + Reasoning;
}

/// <summary>
/// The harness compacted the session's context, or started or failed to. The payload of
/// <see cref="EventTypes.ContextCompaction"/>.
/// </summary>
public sealed record ContextCompactionReport
{
    /// <summary>One of <see cref="ContextCompactionPhases"/>.</summary>
    public required string Phase { get; init; }

    /// <summary>One of <see cref="ContextCompactionTriggers"/>, when the harness says.</summary>
    public string? Trigger { get; init; }

    /// <summary>Why it failed, for <see cref="ContextCompactionPhases.Failed"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>Where a compaction is: <see cref="ContextCompactionReport.Phase"/>.</summary>
public static class ContextCompactionPhases
{
    public const string Started = "started";
    public const string Ended = "ended";
    public const string Failed = "failed";
}

/// <summary>What started a compaction: <see cref="ContextCompactionReport.Trigger"/>.</summary>
public static class ContextCompactionTriggers
{
    /// <summary>The harness, because the context was nearly full.</summary>
    public const string Auto = "auto";

    /// <summary>Someone asked for it (Compact now, or <c>/compact</c>).</summary>
    public const string Manual = "manual";
}

/// <summary>What Fleet passes a harness to compact a session's context (<see cref="IHarnessSession.CompactAsync"/>).</summary>
public sealed record CompactOptions
{
    /// <summary>The session's chosen model, for a harness that summarises with a named model (OpenCode).</summary>
    public string? ProviderId { get; init; }

    public string? ModelId { get; init; }
}
