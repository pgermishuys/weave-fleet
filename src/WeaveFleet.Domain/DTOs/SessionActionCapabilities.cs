namespace WeaveFleet.Domain.DTOs;

public sealed record SessionActionCapabilities(
    bool CanPrompt,
    bool CanRestart,
    bool CanAbort,
    bool CanArchive,
    bool CanUnarchive,
    bool CanFork,
    bool CanDelete,
    string? PromptDisabledReason,
    string? RestartDisabledReason,
    string? AbortDisabledReason,
    string? ArchiveDisabledReason,
    string? UnarchiveDisabledReason,
    string? ForkDisabledReason,
    string? DeleteDisabledReason)
{
    /// <summary>Compact now: the harness can compact the session's context, and the session isn't in a turn.</summary>
    public bool CanCompact { get; init; }

    public string? CompactDisabledReason { get; init; }
}
