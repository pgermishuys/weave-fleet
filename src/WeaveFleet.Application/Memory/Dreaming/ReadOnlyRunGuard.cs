using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>
/// A dreaming session reads and changes nothing: of Fleet's own tools it may call <c>fleet_session_read</c> only, on every
/// harness, whatever the harness lets through itself. Fleet's tools reach it over MCP (<c>FleetToolCalls</c>) or through
/// the plugins' calls to <c>/api/bridge/*</c>; both ask this guard.
/// </summary>
public sealed class ReadOnlyRunGuard(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ISessionRepository sessions)
{
    public const string Refusal = "Dreaming reads and changes nothing, so this tool isn't available in a dreaming session. Only fleet_session_read is.";

    /// <summary>The bridge route (after <c>/api/bridge/</c>) of the one tool a dreaming session may call.</summary>
    public const string AllowedRoute = "session/read";

    /// <summary>Whether <paramref name="session"/>, or the session it's a subagent of, is a dreaming session.</summary>
    public static async Task<bool> IsDreamingAsync(ISessionRepository sessions, Session session)
    {
        var current = session;
        for (var walked = 0; walked < 8; walked++)
        {
            if (current.Tags.Contains(DreamRules.SessionTag))
                return true;
            if (current.ParentSessionId is not { } parentId || await sessions.GetByIdAsync(parentId).ConfigureAwait(false) is not { } parent)
                return false;
            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Whether a call of <paramref name="tool"/> from the process with <paramref name="bridgeToken"/> must be refused: the
    /// call is from a dreaming session (or its subagent) and the tool isn't <c>fleet_session_read</c>. A call Fleet can't
    /// place isn't refused here; the tool refuses it as an unknown caller.
    /// </summary>
    public async Task<bool> RefusesAsync(string? bridgeToken, string? harnessSessionId, string tool, CancellationToken ct = default)
    {
        if (string.Equals(tool, PermissionPolicy.ReadOnlyFleetTool, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return false;

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false);
        if (caller is null)
            return false;

        using (userScope.Begin(caller.UserId))
            return await sessions.GetByIdAsync(caller.FleetSessionId).ConfigureAwait(false) is { } session
                && await IsDreamingAsync(sessions, session).ConfigureAwait(false);
    }
}
