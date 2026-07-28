using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Services;

public sealed class SessionCapabilitiesResolver(InstanceTracker instanceTracker)
{
    public SessionActionCapabilities Resolve(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Resolve(
            session.RuntimeMode,
            session.LifecycleStatus,
            session.RetentionStatus,
            session.ActivityStatus,
            instanceTracker.Get(session.InstanceId) is not null);
    }

    public static SessionActionCapabilities Resolve(
        string? runtimeMode,
        string? lifecycleStatus,
        string? retentionStatus,
        string? activityStatus,
        bool isLive)
    {
        var normalizedRuntimeMode = Normalize(runtimeMode, "manual");
        var normalizedRetentionStatus = Normalize(retentionStatus, "active");
        var effectiveLifecycleStatus = GetEffectiveLifecycleStatus(lifecycleStatus, isLive);
        var isArchived = string.Equals(normalizedRetentionStatus, "archived", StringComparison.Ordinal);
        // Runtime mode is the source of truth for lazy activation. NuCode currently persists
        // sessions as manual, so only the explicit automatic value suppresses Resume in favor
        // of prompt-triggered activation.
        var isAutomatic = string.Equals(normalizedRuntimeMode, "automatic", StringComparison.Ordinal);
        var isRunning = string.Equals(effectiveLifecycleStatus, "running", StringComparison.Ordinal);
        var isBusy = string.Equals(activityStatus, "busy", StringComparison.Ordinal);
        var canPrompt = !isArchived && (isRunning || isAutomatic && IsAutomaticPromptableTerminal(effectiveLifecycleStatus));
        var canStop = !isArchived && isRunning;
        var canResume = !isArchived && !isAutomatic && IsManualResumableTerminal(effectiveLifecycleStatus);
        var canRestart = !isArchived;
        var canAbort = !isArchived && isRunning && isBusy;
        var canArchive = !isArchived;
        var canUnarchive = isArchived;
        var canFork = !isArchived;
        const bool canDelete = true;

        return new SessionActionCapabilities(
            CanPrompt: canPrompt,
            CanStop: canStop,
            CanResume: canResume,
            CanRestart: canRestart,
            CanAbort: canAbort,
            CanArchive: canArchive,
            CanUnarchive: canUnarchive,
            CanFork: canFork,
            CanDelete: canDelete,
            PromptDisabledReason: canPrompt ? null : GetPromptDisabledReason(isArchived, isAutomatic, effectiveLifecycleStatus),
            StopDisabledReason: canStop ? null : GetStopDisabledReason(isArchived, effectiveLifecycleStatus),
            ResumeDisabledReason: canResume ? null : GetResumeDisabledReason(isArchived, isAutomatic, effectiveLifecycleStatus),
            RestartDisabledReason: canRestart ? null : GetArchivedReadOnlyReason(isArchived),
            AbortDisabledReason: canAbort ? null : GetAbortDisabledReason(isArchived, isRunning, isBusy),
            ArchiveDisabledReason: canArchive ? null : GetAlreadyArchivedReason(isArchived),
            UnarchiveDisabledReason: canUnarchive ? null : "Session is not archived.",
            ForkDisabledReason: canFork ? null : GetArchivedReadOnlyReason(isArchived),
            DeleteDisabledReason: null);
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string GetEffectiveLifecycleStatus(string? lifecycleStatus, bool isLive)
    {
        var normalized = Normalize(lifecycleStatus, "running");
        return string.Equals(normalized, "running", StringComparison.Ordinal) && !isLive
            ? "disconnected"
            : normalized;
    }

    private static bool IsAutomaticPromptableTerminal(string lifecycleStatus) =>
        lifecycleStatus is "stopped" or "disconnected" or "completed";

    private static bool IsManualResumableTerminal(string lifecycleStatus) =>
        lifecycleStatus is "stopped" or "disconnected";

    private static string? GetArchivedReadOnlyReason(bool isArchived) =>
        isArchived ? "Archived sessions are read-only." : null;

    private static string? GetAlreadyArchivedReason(bool isArchived) =>
        isArchived ? "Session is already archived." : null;

    private static string? GetPromptDisabledReason(bool isArchived, bool isAutomatic, string lifecycleStatus)
    {
        if (isArchived)
            return "Archived sessions are read-only.";

        if (isAutomatic && IsAutomaticPromptableTerminal(lifecycleStatus))
            return null;

        return "Resume the session before prompting.";
    }

    private static string? GetStopDisabledReason(bool isArchived, string lifecycleStatus)
    {
        if (isArchived)
            return "Archived sessions are read-only.";

        return string.Equals(lifecycleStatus, "running", StringComparison.Ordinal)
            ? null
            : "Session is not running.";
    }

    private static string? GetResumeDisabledReason(bool isArchived, bool isAutomatic, string lifecycleStatus)
    {
        if (isArchived)
            return "Archived sessions cannot be resumed.";

        if (isAutomatic)
            return "Automatic sessions resume on the next prompt.";

        return IsManualResumableTerminal(lifecycleStatus)
            ? null
            : "Session is not resumable.";
    }

    private static string? GetAbortDisabledReason(bool isArchived, bool isRunning, bool isBusy)
    {
        if (isArchived)
            return "Archived sessions are read-only.";

        if (!isRunning)
            return "Session is not running.";

        return isBusy ? null : "Session is not busy.";
    }
}
