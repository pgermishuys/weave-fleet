using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

public sealed class SessionCapabilitiesResolver(
    InstanceTracker instanceTracker,
    SessionActivityTracker activityTracker,
    IHarnessRegistry? harnessRegistry = null)
{
    public SessionActionCapabilities Resolve(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Resolve(
            session.LifecycleStatus,
            session.RetentionStatus,
            activityTracker.GetEffectiveActivityStatus(session.Id) ?? "idle",
            instanceTracker.Get(session.InstanceId) is not null,
            harnessRegistry is null ? null : ForkUnsupportedReason(harnessRegistry.GetByType(session.HarnessType)));
    }

    /// <summary>
    /// Why sessions on <paramref name="harness"/> can't be forked, or null when they can: a fork is a copy of the
    /// conversation in the harness (<see cref="HarnessCapabilities.SupportsForking"/>), and a harness that can't copy one
    /// has nothing to fork.
    /// </summary>
    public static string? ForkUnsupportedReason(IHarness? harness) => harness switch
    {
        null => null,
        { Capabilities.SupportsForking: true } => null,
        _ => $"{harness.DisplayName} can't copy a conversation, so its sessions can't be forked.",
    };

    /// <param name="forkUnsupportedReason">
    /// Why the session's harness can't fork (<see cref="ForkUnsupportedReason"/>); null when it can.
    /// </param>
    public static SessionActionCapabilities Resolve(
        string? lifecycleStatus,
        string? retentionStatus,
        string? activityStatus,
        bool isLive,
        string? forkUnsupportedReason = null)
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
        var canFork = !isArchived && forkUnsupportedReason is null;
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
            ForkDisabledReason: canFork ? null : GetArchivedReadOnlyReason(isArchived) ?? forkUnsupportedReason,
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
