using System.Text.Json;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Keeps a session's stored branch in step with the folder it works in: an agent that runs <c>git switch</c> there
/// moves the session's row, header and pull request link to the new branch, not only at worktree cleanup. Scoped.
/// </summary>
public sealed class SessionBranchService(
    ISessionRepository sessions,
    IWorkspaceRepository workspaces,
    GitDiffService git,
    IEventBroadcaster broadcaster,
    IUserContext userContext)
{
    /// <summary>Raised on the <c>sessions</c> topic when a workspace's branch changed.</summary>
    public const string BranchEventType = "session_branch";

    /// <summary>
    /// Reads the branch checked out in the session's folder and stores and broadcasts it when it differs. A folder that's
    /// gone, isn't in git, or has a detached HEAD leaves the stored branch alone, and so does a folder with no stored branch
    /// (one the user opened as it was; only the checkouts Fleet made carry one).
    /// </summary>
    public async Task RefreshAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null || string.IsNullOrEmpty(session.WorkspaceId))
            return;

        var workspace = await workspaces.GetByIdAsync(session.WorkspaceId).ConfigureAwait(false);
        if (workspace is null || workspace.CleanedUpAt is not null || workspace.Branch is null)
            return;

        var branch = await git.CurrentBranchAsync(workspace.Directory, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(branch) || branch == workspace.Branch)
            return;

        await workspaces.UpdateBranchAsync(workspace.Id, branch).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(
            new SessionBranchPayload(workspace.Id, branch), ApplicationJsonContext.Default.SessionBranchPayload);
        await broadcaster.BroadcastAsync("sessions", BranchEventType, payload, userContext.UserId, CancellationToken.None).ConfigureAwait(false);
    }
}

internal sealed record SessionBranchPayload(string WorkspaceId, string Branch);
