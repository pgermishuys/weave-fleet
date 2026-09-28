using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Settings → Permissions, as the owner's preferences keep it, and the policy a session gets from it. Unset, every level
/// is <see cref="PermissionLevels.All"/>: nothing asks until the user chooses otherwise.
/// </summary>
public static class SessionPermissions
{
    /// <summary>The level every harness uses unless one is set for it.</summary>
    public const string LevelKey = "PermissionLevel";

    /// <summary>What happens in a run nobody watches (<see cref="UnattendedPermissions"/>).</summary>
    public const string UnattendedKey = "PermissionUnattended";

    /// <summary>The key for <paramref name="harnessType"/>'s own level, e.g. <c>PermissionLevel.claude-code</c>.</summary>
    public static string HarnessLevelKey(string harnessType) => $"{LevelKey}.{harnessType}";

    private const string AutomationSource = "automation:";
    private const int MaxParentsWalked = 8;

    /// <summary>
    /// The policy for <paramref name="session"/>: its harness's level, or the default; in a run nobody watches (an
    /// automation's session, a workflow step Fleet finishes, or a subagent of either), what the unattended setting says.
    /// </summary>
    public static async Task<PermissionPolicy> ResolveAsync(
        IUserPreferenceRepository preferences,
        ISessionRepository sessions,
        Session session)
    {
        var level = await preferences.GetAsync(HarnessLevelKey(session.HarnessType)).ConfigureAwait(false);
        if (!PermissionLevels.IsKnown(level))
            level = await preferences.GetAsync(LevelKey).ConfigureAwait(false);
        if (!PermissionLevels.IsKnown(level))
            level = PermissionLevels.All;

        if (!await IsUnattendedAsync(sessions, session).ConfigureAwait(false))
            return new PermissionPolicy(level!);

        return await preferences.GetAsync(UnattendedKey).ConfigureAwait(false) switch
        {
            UnattendedPermissions.Same => new PermissionPolicy(level!),
            UnattendedPermissions.Deny => new PermissionPolicy(level!, RejectAsks: true),
            _ => PermissionPolicy.AllowAll,
        };
    }

    /// <summary>Whether nobody watches the session's run: it, or the session it's a subagent of, was started by Fleet.</summary>
    private static async Task<bool> IsUnattendedAsync(ISessionRepository sessions, Session session)
    {
        var current = session;
        for (var walked = 0; walked < MaxParentsWalked; walked++)
        {
            if (current.SourceReference?.StartsWith(AutomationSource, StringComparison.Ordinal) == true
                || (current.WorkflowRunId is not null && !current.WorkflowUserFinishes))
            {
                return true;
            }

            if (current.ParentSessionId is not { } parentId || await sessions.GetByIdAsync(parentId).ConfigureAwait(false) is not { } parent)
                return false;
            current = parent;
        }

        return false;
    }
}
