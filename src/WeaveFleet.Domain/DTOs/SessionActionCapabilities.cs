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
    string? DeleteDisabledReason);
