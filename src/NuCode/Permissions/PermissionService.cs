using System.Collections.Concurrent;

namespace NuCode.Permissions;

/// <summary>
/// Default implementation of <see cref="IPermissionService"/>.
/// Manages pending permission requests with a deferred ask/reply pattern.
/// </summary>
internal sealed class PermissionService : IPermissionService
{
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();
    private readonly object _approvedLock = new();
    private PermissionRuleset _approved = new() { Name = "session-approved" };

    public async Task RequestPermissionAsync(
        SessionId sessionId,
        string permission,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> alwaysPatterns,
        IReadOnlyList<PermissionRuleset> rulesets,
        CancellationToken cancellationToken)
    {
        var needsAsk = false;

        // Build combined rulesets including session-approved
        var allRulesets = new List<PermissionRuleset>(rulesets) { _approved };

        foreach (var pattern in patterns)
        {
            var action = PermissionEvaluator.Evaluate(permission, pattern, [.. allRulesets]);

            if (action == PermissionAction.Deny)
            {
                throw new PermissionDeniedException(permission, pattern);
            }

            if (action == PermissionAction.Ask)
            {
                needsAsk = true;
            }
        }

        if (!needsAsk)
        {
            return;
        }

        // Create pending request
        var requestId = Ulid.NewUlid().ToString();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PermissionRequest
        {
            Id = requestId,
            SessionId = sessionId,
            Permission = permission,
            Patterns = patterns,
            AlwaysPatterns = alwaysPatterns,
        };

        var entry = new PendingEntry(request, tcs);
        _pending[requestId] = entry;

        try
        {
            using var registration = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public void ReplyToPermission(string requestId, PermissionDecision decision)
    {
        if (!_pending.TryGetValue(requestId, out var entry))
        {
            return;
        }

        switch (decision)
        {
            case PermissionDecision.Allow:
                entry.Completion.TrySetResult();
                break;

            case PermissionDecision.AlwaysAllow:
                // Add rules to the approved ruleset
                lock (_approvedLock)
                {
                    var rules = entry.Request.AlwaysPatterns
                        .Select(p => new PermissionRule(entry.Request.Permission, p, PermissionAction.Allow))
                        .ToArray();
                    _approved = _approved.WithRules(rules);
                }
                entry.Completion.TrySetResult();

                // Auto-approve other pending requests from the same session
                AutoApprovePending(entry.Request.SessionId);
                break;

            case PermissionDecision.Deny:
                entry.Completion.TrySetException(
                    new PermissionDeniedException(entry.Request.Permission, string.Join(", ", entry.Request.Patterns)));

                // Reject all other pending requests from the same session
                RejectSessionRequests(entry.Request.SessionId, requestId);
                break;
        }
    }

    public IReadOnlyList<PermissionRequest> GetPendingRequests() =>
        _pending.Values.Select(e => e.Request).ToList();

    public PermissionRuleset GetApprovedRuleset() => _approved;

    private void AutoApprovePending(SessionId sessionId)
    {
        foreach (var (_, pendingEntry) in _pending)
        {
            if (pendingEntry.Request.SessionId != sessionId)
            {
                continue;
            }

            var allAllowed = pendingEntry.Request.Patterns.All(pattern =>
                PermissionEvaluator.Evaluate(pendingEntry.Request.Permission, pattern, _approved)
                == PermissionAction.Allow);

            if (allAllowed)
            {
                pendingEntry.Completion.TrySetResult();
            }
        }
    }

    private void RejectSessionRequests(SessionId sessionId, string excludeRequestId)
    {
        foreach (var (id, pendingEntry) in _pending)
        {
            if (id == excludeRequestId || pendingEntry.Request.SessionId != sessionId)
            {
                continue;
            }

            pendingEntry.Completion.TrySetException(
                new PermissionDeniedException(pendingEntry.Request.Permission, "Session permission denied"));
        }
    }

    private sealed record PendingEntry(PermissionRequest Request, TaskCompletionSource Completion);
}
