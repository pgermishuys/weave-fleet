namespace NuCode.Permissions;

/// <summary>
/// Manages permission requests and decisions. Supports a deferred ask/reply pattern
/// where tool execution pauses until the user responds.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Checks permission for the given patterns against the rulesets.
    /// If all patterns are allowed, returns immediately.
    /// If any pattern is denied, throws.
    /// If any pattern needs asking, creates a pending request and blocks until the user replies.
    /// </summary>
    /// <param name="sessionId">The current session.</param>
    /// <param name="permission">The permission type.</param>
    /// <param name="patterns">The patterns to check.</param>
    /// <param name="alwaysPatterns">The patterns to add as rules if "always allow" is chosen.</param>
    /// <param name="rulesets">The rulesets to evaluate against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when permission is granted or throws if denied.</returns>
    Task RequestPermissionAsync(
        SessionId sessionId,
        string permission,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> alwaysPatterns,
        IReadOnlyList<PermissionRuleset> rulesets,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replies to a pending permission request.
    /// </summary>
    /// <param name="requestId">The ID of the pending request.</param>
    /// <param name="decision">The user's decision.</param>
    void ReplyToPermission(string requestId, PermissionDecision decision);

    /// <summary>
    /// Gets all pending permission requests.
    /// </summary>
    IReadOnlyList<PermissionRequest> GetPendingRequests();

    /// <summary>
    /// Gets the session-approved ruleset (rules added via "always allow" decisions).
    /// </summary>
    PermissionRuleset GetApprovedRuleset();
}
