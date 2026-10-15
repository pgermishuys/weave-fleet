using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workspaces;

/// <summary>
/// Removes the checkouts of worktrees Fleet made for sessions archived a week or more, and says why it keeps the
/// rest. Only the folder goes: the branch with every commit, the conversation and the session stay, and the next wake
/// puts the checkout back (<see cref="WorkspaceService.RestoreCheckoutAsync"/>). The remove is a plain
/// <c>git worktree remove</c>, so git itself refuses a checkout with uncommitted work, after Fleet's own checks.
/// </summary>
public sealed partial class WorktreeCleanup(
    WorkspaceService workspaceService,
    IWorkspaceRepository workspaces,
    ISessionRepository sessions,
    InstanceTracker instanceTracker,
    SessionActivityTracker activityTracker,
    IEventBroadcaster eventBroadcaster,
    ILogger<WorktreeCleanup> logger,
    ISessionTerminalCleanup? terminals = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>How long a session is archived before its checkout may go.</summary>
    public const int ArchivedDays = 7;

    /// <summary>Raised on the <c>sessions</c> topic when a workspace's checkout is removed, being put back, or back.</summary>
    public const string CheckoutEventType = "session_checkout";

    /// <summary>
    /// Ignored files that are rebuilt, so they never keep a checkout. Any other ignored file (a stray <c>.env</c>) does.
    /// </summary>
    private static readonly HashSet<string> Rebuildable = new(StringComparer.Ordinal)
    {
        "node_modules", "dist", "build", "target", "bin", "obj", ".next", ".venv", "__pycache__",
    };

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Every checkout of a worktree Fleet made that is still on disk, with what Clean now would do to it.</summary>
    public async Task<IReadOnlyList<WorktreeCheckout>> ListAsync(CancellationToken ct = default)
    {
        var context = await LoadAsync(ct).ConfigureAwait(false);
        var results = new ConcurrentBag<WorktreeCheckout>();
        await Parallel.ForEachAsync(context.Worktrees, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (workspace, token) =>
            {
                if (await EvaluateAsync(workspace, context, token).ConfigureAwait(false) is { } checkout)
                    results.Add(checkout);
            }).ConfigureAwait(false);

        return [.. results.OrderBy(c => c.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Removes the checkouts of <paramref name="workspaceIds"/> that may still go, checking each again first: what
    /// changed since the preview (a message, a new file) keeps it.
    /// </summary>
    public async Task<WorktreeCleanupReport> CleanAsync(IReadOnlyCollection<string> workspaceIds, CancellationToken ct = default)
    {
        var context = await LoadAsync(ct).ConfigureAwait(false);
        var removed = new List<WorktreeCheckout>();
        var kept = new List<WorktreeCheckout>();
        foreach (var workspace in context.Worktrees.Where(w => workspaceIds.Contains(w.Id)))
        {
            if (await EvaluateAsync(workspace, context, ct).ConfigureAwait(false) is not { } checkout)
                continue;

            var outcome = checkout.Verdict == CheckoutVerdicts.Remove
                ? await RemoveAsync(workspace, context.SessionsByWorkspace[workspace.Id], checkout, ct).ConfigureAwait(false)
                : checkout;
            (outcome.Verdict == CheckoutVerdicts.Remove ? removed : kept).Add(outcome);
        }

        return new WorktreeCleanupReport(removed, kept);
    }

    /// <summary>
    /// How much the workspace's folder holds on disk, measured once per change of the folder (its modified time), and
    /// at most <paramref name="maxEntries"/> files and folders deep: <see cref="FolderSize.Complete"/> says whether it
    /// got to the end.
    /// </summary>
    public async Task<FolderSize?> MeasureAsync(string workspaceId, int maxEntries = 400_000)
    {
        var workspace = await workspaces.GetByIdAsync(workspaceId).ConfigureAwait(false);
        if (workspace is not { IsolationStrategy: "worktree" } || !Directory.Exists(workspace.Directory))
            return null;

        var stamp = Directory.GetLastWriteTimeUtc(workspace.Directory);
        if (Sizes.TryGetValue(workspace.Directory, out var cached) && cached.Stamp == stamp)
            return cached.Size;

        var size = await MeasureFolderAsync(workspace.Directory, maxEntries).ConfigureAwait(false);
        Sizes[workspace.Directory] = (stamp, size);
        return size;
    }

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, FolderSize Size)> Sizes = new(StringComparer.Ordinal);

    internal static Task<FolderSize> MeasureFolderAsync(string root, int maxEntries) => Task.Run(() => Measure(root, maxEntries));

    private static FolderSize Measure(string root, int maxEntries)
    {
        long bytes = 0;
        var entries = 0;
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (++entries > maxEntries)
                    return new FolderSize(bytes, Complete: false);
                if (entry is FileInfo file)
                    bytes += file.Length;
                else if (entry is DirectoryInfo child)
                    pending.Push(child);
            }
        }

        return new FolderSize(bytes, Complete: true);
    }

    private sealed record CleanupContext(
        IReadOnlyList<Workspace> Worktrees,
        IReadOnlyDictionary<string, IReadOnlyList<Session>> SessionsByWorkspace,
        IReadOnlySet<string> SharedFolders);

    private async Task<CleanupContext> LoadAsync(CancellationToken ct)
    {
        var all = await workspaces.ListAsync().ConfigureAwait(false);
        var worktrees = all.Where(w => w is { IsolationStrategy: "worktree", CleanedUpAt: null }).ToList();
        var sessionsByWorkspace = new Dictionary<string, IReadOnlyList<Session>>(StringComparer.Ordinal);
        foreach (var workspace in worktrees)
            sessionsByWorkspace[workspace.Id] = await sessions.GetForWorkspaceAsync(workspace.Id).ConfigureAwait(false);

        // A fork, or a kept side conversation, gets a workspace of its own over the same folder.
        var shared = new HashSet<string>(StringComparer.Ordinal);
        var worktreeFolders = worktrees.Select(w => w.Directory).ToHashSet(StringComparer.Ordinal);
        foreach (var other in all.Where(w => w.IsolationStrategy != "worktree" && w.CleanedUpAt is null && worktreeFolders.Contains(w.Directory)))
        {
            ct.ThrowIfCancellationRequested();
            if ((await sessions.GetForWorkspaceAsync(other.Id).ConfigureAwait(false)).Count > 0)
                shared.Add(other.Directory);
        }

        return new CleanupContext(worktrees, sessionsByWorkspace, shared);
    }

    private async Task<WorktreeCheckout?> EvaluateAsync(Workspace workspace, CleanupContext context, CancellationToken ct)
    {
        var inWorkspace = context.SessionsByWorkspace[workspace.Id];

        // Subagents and side conversations work in their session's folder; the session they belong to decides.
        var owners = inWorkspace.Where(s => s.ParentSessionId is null && s.SideOfSessionId is null).OrderBy(s => s.CreatedAt, StringComparer.Ordinal).ToList();
        if (owners.Count == 0 || workspace.SourceDirectory is null)
            return null;

        var owner = owners[0];
        var checkout = new WorktreeCheckout(
            workspace.Id, owner.Id, owner.Title, Path.GetFileName(workspace.SourceDirectory.TrimEnd('/', '\\')),
            workspace.Directory, workspace.Branch, CheckoutVerdicts.Remove, "", null, owner.ArchivedAt);
        WorktreeCheckout Keep(string reason) => checkout with { Verdict = CheckoutVerdicts.Keep, Reason = reason };

        if (!Directory.Exists(workspace.Directory))
            return checkout with { Reason = "The folder is already gone." };

        if (owner.RetentionStatus != "archived" || !DateTimeOffset.TryParse(owner.ArchivedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var archivedAt))
            return checkout with { Verdict = CheckoutVerdicts.InUse, Reason = "The session isn't archived." };

        var archivedDays = (int)(_time.GetUtcNow() - archivedAt).TotalDays;
        var archived = archivedDays switch
        {
            0 => "Archived today.",
            1 => "Archived yesterday.",
            _ => $"Archived {archivedDays} days ago.",
        };
        if (archivedDays < ArchivedDays)
            return checkout with { Verdict = CheckoutVerdicts.Recent, Reason = archived };

        if (owners.Count > 1 || context.SharedFolders.Contains(workspace.Directory))
            return Keep("Another session uses this folder.");

        if (inWorkspace.FirstOrDefault(s => SessionActivityTracker.IsInTurn(activityTracker.GetEffectiveActivityStatus(s.Id))) is { } working)
            return Keep(working.Id == owner.Id ? "The agent is working." : "A subagent is working.");

        if (terminals is not null && inWorkspace.Any(s => terminals.HasOpenTerminal(s.Id)))
            return Keep("A terminal is open in it.");

        try
        {
            var status = await GitCommand.RunAsync(workspace.Directory, GitTimeout, "status", "--porcelain", "-z", "--ignored=matching").ConfigureAwait(false);
            var (changed, strayIgnored) = ReadStatus(status);
            if (changed > 0)
                return Keep(changed == 1 ? "Has uncommitted changes (1 file)." : $"Has uncommitted changes ({changed} files).");
            if (strayIgnored is not null)
                return Keep($"Has an ignored file you may want: {strayIgnored}");

            var branch = (await TryGitAsync(workspace.Directory, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(branch))
                return Keep("It isn't on a branch, so it couldn't come back.");

            var unpushed = await CountUnpushedAsync(workspace.Directory).ConfigureAwait(false);
            var reason = unpushed switch
            {
                null or 0 => archived,
                1 => $"{archived} 1 commit only on this machine, kept on {branch}.",
                _ => $"{archived} {unpushed} commits only on this machine, kept on {branch}.",
            };
            return checkout with { Branch = branch, Reason = reason, UnpushedCommits = unpushed };
        }
        catch (GitCommandException ex)
        {
            return Keep($"Fleet couldn't read it: {ex.GitMessage}");
        }
    }

    /// <summary>
    /// What <c>git status --porcelain -z --ignored=matching</c> says: how many paths have changes (tracked edits or untracked
    /// files), and the first ignored path that isn't rebuilt output, if any.
    /// </summary>
    internal static (int Changed, string? StrayIgnored) ReadStatus(string porcelain)
    {
        var changed = 0;
        string? stray = null;
        var entries = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
                continue;

            var code = entry[..2];
            var path = entry[3..];
            if (code == "!!")
            {
                var segments = path.TrimEnd('/').Split('/');
                if (stray is null && !segments.Any(Rebuildable.Contains))
                    stray = path.TrimEnd('/');
                continue;
            }

            changed++;
            if (code[0] is 'R' or 'C')
                i++; // a rename's source path follows as its own entry
        }

        return (changed, stray);
    }

    private static async Task<int?> CountUnpushedAsync(string directory)
    {
        // With no remote there's nowhere a commit could be pushed, so nothing to count.
        if (string.IsNullOrWhiteSpace(await TryGitAsync(directory, "remote").ConfigureAwait(false)))
            return null;

        var count = await TryGitAsync(directory, "rev-list", "--count", "HEAD", "--not", "--remotes").ConfigureAwait(false);
        return int.TryParse(count?.Trim(), CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private async Task<WorktreeCheckout> RemoveAsync(Workspace workspace, IReadOnlyList<Session> inWorkspace, WorktreeCheckout checkout, CancellationToken ct)
    {
        // The harness lets go of the folder first; the session wakes on its next message, as after a restart.
        foreach (var session in inWorkspace)
            await ReleaseAsync(session, ct).ConfigureAwait(false);

        var repository = workspace.SourceDirectory!;
        try
        {
            if (Directory.Exists(workspace.Directory))
                await GitCommand.RunAsync(repository, GitTimeout, "worktree", "remove", workspace.Directory).ConfigureAwait(false);
            else
                await GitCommand.RunAsync(repository, GitTimeout, "worktree", "prune").ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            LogRemoveRefused(workspace.Directory, ex.GitMessage);
            return checkout with { Verdict = CheckoutVerdicts.Keep, Reason = $"Git kept it: {ex.GitMessage}" };
        }

        if (checkout.Branch is { } branch && branch != workspace.Branch)
            await workspaces.UpdateBranchAsync(workspace.Id, branch).ConfigureAwait(false);
        await workspaces.MarkCleanedAsync(workspace.Id).ConfigureAwait(false);
        Sizes.TryRemove(workspace.Directory, out _);
        LogRemoved(workspace.Directory, checkout.Branch ?? "");

        await BroadcastCheckoutAsync(eventBroadcaster, workspace.Id, CheckoutStates.Removed,
            _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), inWorkspace[0].UserId, ct).ConfigureAwait(false);
        return checkout;
    }

    private async Task ReleaseAsync(Session session, CancellationToken ct)
    {
        if (instanceTracker.Get(session.InstanceId) is not { } instance)
            return;

        try { await instance.StopAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogReleaseFailed(ex, session.Id); }
        instanceTracker.Remove(session.InstanceId);
    }

    /// <summary>Puts a removed checkout back now (the session header's Restore now), rather than on the next message.</summary>
    public async Task<Result<Unit>> RestoreAsync(string workspaceId, string userId, CancellationToken ct = default)
    {
        var workspace = await workspaces.GetByIdAsync(workspaceId).ConfigureAwait(false);
        if (workspace is null)
            return FleetError.NotFoundFor(nameof(Workspace), workspaceId);

        return await RestoreAsync(workspaceService, eventBroadcaster, workspace, userId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a checkout Clean now removed back, at the same path, saying so on the session header while it happens.
    /// Does nothing for a checkout that's there.
    /// </summary>
    internal static async Task<Result<Unit>> RestoreAsync(
        WorkspaceService workspaceService, IEventBroadcaster broadcaster, Workspace workspace, string userId, CancellationToken ct)
    {
        if (workspace is not { IsolationStrategy: "worktree", CleanedUpAt: { } removedAt })
            return Unit.Value;

        await BroadcastCheckoutAsync(broadcaster, workspace.Id, CheckoutStates.Restoring, removedAt, userId, ct).ConfigureAwait(false);
        var restored = await workspaceService.RestoreCheckoutAsync(workspace).ConfigureAwait(false);
        await BroadcastCheckoutAsync(
            broadcaster,
            workspace.Id,
            restored.IsSuccess ? CheckoutStates.Restored : CheckoutStates.Removed,
            restored.IsSuccess ? null : removedAt,
            userId,
            ct).ConfigureAwait(false);
        return restored;
    }

    /// <summary>Tells every open Fleet that a workspace's checkout is removed, being put back, or back.</summary>
    internal static Task BroadcastCheckoutAsync(
        IEventBroadcaster broadcaster, string workspaceId, string state, string? removedAt, string userId, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new SessionCheckoutPayload(workspaceId, state, removedAt), ApplicationJsonContext.Default.SessionCheckoutPayload);
        return broadcaster.BroadcastAsync("sessions", CheckoutEventType, payload, userId, ct);
    }

    private static async Task<string?> TryGitAsync(string directory, params string[] args)
    {
        try { return await GitCommand.RunAsync(directory, GitTimeout, args).ConfigureAwait(false); }
        catch (GitCommandException) { return null; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the checkout {Directory}; branch {Branch} kept")]
    private partial void LogRemoved(string directory, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Git kept the checkout {Directory}: {Reason}")]
    private partial void LogRemoveRefused(string directory, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't stop session {SessionId}'s harness before removing its checkout")]
    private partial void LogReleaseFailed(Exception ex, string sessionId);
}

/// <summary>What Clean now does to a checkout.</summary>
public static class CheckoutVerdicts
{
    public const string Remove = "remove";
    public const string Keep = "keep";
    /// <summary>Archived, but not for <see cref="WorktreeCleanup.ArchivedDays"/> yet.</summary>
    public const string Recent = "recent";
    /// <summary>The session isn't archived.</summary>
    public const string InUse = "in-use";
}

/// <summary>Where a workspace's checkout is, for the session header.</summary>
public static class CheckoutStates
{
    public const string Removed = "removed";
    public const string Restoring = "restoring";
    public const string Restored = "restored";
}

/// <summary>
/// A worktree checkout and what Clean now does with it (<see cref="CheckoutVerdicts"/>), with a sentence that says why.
/// </summary>
/// <param name="Repository">The repository's folder name.</param>
/// <param name="UnpushedCommits">Commits on the branch no remote has; null when the repository has no remote.</param>
public sealed record WorktreeCheckout(
    string WorkspaceId,
    string SessionId,
    string Title,
    string Repository,
    string Directory,
    string? Branch,
    string Verdict,
    string Reason,
    int? UnpushedCommits,
    string? ArchivedAt);

public sealed record WorktreeCleanupReport(IReadOnlyList<WorktreeCheckout> Removed, IReadOnlyList<WorktreeCheckout> Kept);

/// <summary>A folder's size in bytes; not complete when Fleet stopped counting at its cap.</summary>
public sealed record FolderSize(long Bytes, bool Complete);

internal sealed record SessionCheckoutPayload(string WorkspaceId, string State, string? RemovedAt);
