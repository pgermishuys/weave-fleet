using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Which session came from which, as far as Fleet's rules care: how many agent-starts a session is below one the user
/// started, and the error an agent gets when it would go deeper. See *Lineage* in
/// <c>docs/background-work-and-lineage.md</c>.
/// </summary>
public static class SessionLineage
{
    /// <summary>
    /// How deep agents may start sessions: a session this many agent-starts below one the user started can't start
    /// another through Fleet's API. Stops a runaway chain of agents starting agents. Subagents inside a harness are the
    /// harness's business (Claude Code and OpenCode have their own limits) and don't count.
    /// </summary>
    public const int MaxAgentSpawnDepth = 3;

    /// <summary>The longest chain worth walking; a loop of bad data stops here.</summary>
    private const int MaxWalk = 32;

    /// <summary>
    /// How many agent-starts the session is below a session the user started: 0 for the user's own, 1 for one its agent
    /// started, and so on. A subagent's hidden session sits at its parent's depth (it's part of the parent's turn). A
    /// session the user moved out of its parent, a fork and anything else the user started end the chain.
    /// </summary>
    public static async Task<int> AgentSpawnDepthAsync(ISessionRepository sessions, string sessionId)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = sessionId; seen.Count < MaxWalk && seen.Add(id);)
        {
            var session = await sessions.GetByIdAsync(id);
            if (session is null)
                break;
            if (session.ParentSessionId is { } parent)
            {
                id = parent;
                continue;
            }
            if (session.LineageDetachedAt is not null || session.SpawnedBySessionId is not { } spawnedBy)
                break;
            depth++;
            id = spawnedBy;
        }
        return depth;
    }

    /// <summary>What an agent reads when its session is too deep to start another: what happened and what to do.</summary>
    public static FleetError TooDeep(int depth) => new(
        "General.Conflict",
        $"This session is {depth} levels down from a session the user started; Fleet doesn't let agents start sessions "
        + "deeper than that. Ask the user, or do the work here.");

    /// <summary>Whether the session came from another one it could be moved out of: a fork or a session an agent started.</summary>
    public static bool CanDetach(Session session) =>
        session.ParentSessionId is null && (session.ForkedFromSessionId is not null || session.SpawnedBySessionId is not null);
}
