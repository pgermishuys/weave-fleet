using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Services;

public sealed class SessionCapabilitiesResolver(InstanceTracker instanceTracker, SessionActivityTracker activityTracker)
{
    public SessionActionCapabilities Resolve(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Resolve(
            session.LifecycleStatus,
            session.RetentionStatus,
            activityTracker.GetEffectiveActivityStatus(session.Id) ?? "idle",
            instanceTracker.Get(session.InstanceId) is not null);
    }

    public static SessionActionCapabilities Resolve(
        string? lifecycleStatus,
        string? retentionStatus,
        string? activityStatus,
        bool isLive)
    {
        var normalizedRetentionStatus = Normalize(retentionStatus, "active");
        var effectiveLifecycleStatus = GetEffectiveLifecycleStatus(lifecycleStatus, isLive);
        var isArchived = string.Equals(normalizedRetentionStatus, "archived", StringComparison.Ordinal);
        var isRunning = string.Equals(effectiveLifecycleStatus, "running", StringComparison.Ordinal);
        // A retrying session, and one stopped on a question, are both still in their turn, so they can be stopped.
        var isBusy = SessionActivityTracker.IsInTurn(activityStatus);
        // A session that isn't running wakes on its next prompt.
        var canPrompt = !isArchived && (isRunning || IsPromptableTerminal(effectiveLifecycleStatus));
        var canRestart = !isArchived;
        var canAbort = !isArchived && isRunning && isBusy;
        var canArchive = !isArchived;
        var canUnarchive = isArchived;
        var canFork = !isArchived;
        const bool canDelete = true;

        return new SessionActionCapabilities(
            CanPrompt: canPrompt,
            CanRestart: canRestart,
            CanAbort: canAbort,
            CanArchive: canArchive,
            CanUnarchive: canUnarchive,
            CanFork: canFork,
            CanDelete: canDelete,
            PromptDisabledReason: canPrompt ? null : GetPromptDisabledReason(isArchived),
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

    private static bool IsPromptableTerminal(string lifecycleStatus) =>
        lifecycleStatus is "stopped" or "disconnected" or "completed";

    private static string? GetArchivedReadOnlyReason(bool isArchived) =>
        isArchived ? "Archived sessions are read-only." : null;

    private static string? GetAlreadyArchivedReason(bool isArchived) =>
        isArchived ? "Session is already archived." : null;

    private static string GetPromptDisabledReason(bool isArchived) =>
        isArchived ? "Archived sessions are read-only." : "Session is not running.";

    private static string? GetAbortDisabledReason(bool isArchived, bool isRunning, bool isBusy)
    {
        if (isArchived)
            return "Archived sessions are read-only.";

        if (!isRunning)
            return "Session is not running.";

        return isBusy ? null : "Session is not busy.";
    }
}
