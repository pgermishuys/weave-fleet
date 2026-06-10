namespace WeaveFleet.Domain.DTOs;

public sealed record SessionActionCapabilities(
    bool CanPrompt,
    bool CanStop,
    bool CanResume,
    bool CanRestart,
    bool CanAbort,
    bool CanArchive,
    bool CanUnarchive,
    bool CanFork,
    bool CanDelete,
    string? PromptDisabledReason,
    string? StopDisabledReason,
    string? ResumeDisabledReason,
    string? RestartDisabledReason,
    string? AbortDisabledReason,
    string? ArchiveDisabledReason,
    string? UnarchiveDisabledReason,
    string? ForkDisabledReason,
    string? DeleteDisabledReason);
