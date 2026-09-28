using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// The agents' asks waiting on the user, by the session each is shown on: the session that asked, or for a subagent's,
/// the session it works for. Kept by the relay from the <c>permission.asked</c> and <c>permission.replied</c> events, so a
/// client that opens the session, or reconnects, shows what still waits.
/// </summary>
public interface IPendingPermissions
{
    /// <summary>The asks waiting in session <paramref name="sessionId"/>, oldest first.</summary>
    IReadOnlyList<PermissionAsk> WaitingIn(string sessionId);
}
