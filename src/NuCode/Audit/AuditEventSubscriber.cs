using System.Collections.Concurrent;
using NuCode.Events;
using NuCode.Permissions;

namespace NuCode.Audit;

/// <summary>
/// Subscribes to <see cref="ToolEvents"/> and <see cref="PermissionEvents"/> on the session event bus
/// and writes audit entries via <see cref="IAuditService"/>.
/// Tracks start times keyed by call ID to compute durations.
/// </summary>
internal sealed class AuditEventSubscriber : IDisposable
{
    private readonly IAuditService _auditService;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _startTimes = new();
    private readonly List<IDisposable> _subscriptions = [];

    public AuditEventSubscriber(IAuditService auditService, INuCodeEventBus eventBus)
    {
        _auditService = auditService;
        _subscriptions.Add(eventBus.Subscribe(ToolEvents.Started, OnToolStarted));
        _subscriptions.Add(eventBus.Subscribe(ToolEvents.Completed, OnToolCompleted));
        _subscriptions.Add(eventBus.Subscribe(ToolEvents.Failed, OnToolFailed));
        _subscriptions.Add(eventBus.Subscribe(PermissionEvents.Asked, OnPermissionAsked));
        _subscriptions.Add(eventBus.Subscribe(PermissionEvents.Replied, OnPermissionReplied));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }

    private void OnToolStarted(NuCodeEvent<ToolEvents.ToolStartedInfo> evt)
    {
        var key = evt.Properties.CallId ?? evt.Properties.ToolName;
        _startTimes[key] = DateTimeOffset.UtcNow;
    }

    private void OnToolCompleted(NuCodeEvent<ToolEvents.ToolCompletedInfo> evt)
    {
        var key = evt.Properties.CallId ?? evt.Properties.ToolName;
        _startTimes.TryRemove(key, out var startTime);
        var durationMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

        var entry = new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: evt.Properties.SessionId.Value,
            ToolName: evt.Properties.ToolName,
            ArgsSummary: null,
            Status: "completed",
            DurationMs: durationMs,
            AgentId: null,
            Detail: evt.Properties.Title);

        RecordSafely(() => _auditService.RecordToolInvocationAsync(entry));
    }

    private void OnToolFailed(NuCodeEvent<ToolEvents.ToolFailedInfo> evt)
    {
        var key = evt.Properties.CallId ?? evt.Properties.ToolName;
        _startTimes.TryRemove(key, out var startTime);
        var durationMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

        var entry = new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: evt.Properties.SessionId.Value,
            ToolName: evt.Properties.ToolName,
            ArgsSummary: null,
            Status: "error",
            DurationMs: durationMs,
            AgentId: null,
            Detail: evt.Properties.Error);

        RecordSafely(() => _auditService.RecordToolInvocationAsync(entry));
    }

    private void OnPermissionAsked(NuCodeEvent<PermissionEvents.PermissionAskedInfo> evt)
    {
        var entry = new AuditPermissionEntry(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: evt.Properties.SessionId.Value,
            Permission: evt.Properties.Permission,
            Patterns: evt.Properties.Patterns,
            Decision: "asked");

        RecordSafely(() => _auditService.RecordPermissionDecisionAsync(entry));
    }

    private void OnPermissionReplied(NuCodeEvent<PermissionEvents.PermissionRepliedInfo> evt)
    {
        var decision = evt.Properties.Decision switch
        {
            PermissionDecision.Allow => "allow",
            PermissionDecision.AlwaysAllow => "always-allow",
            PermissionDecision.Deny => "deny",
            _ => evt.Properties.Decision.ToString().ToLowerInvariant(),
        };

        var entry = new AuditPermissionEntry(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: evt.Properties.SessionId.Value,
            Permission: string.Empty,
            Patterns: [],
            Decision: decision);

        RecordSafely(() => _auditService.RecordPermissionDecisionAsync(entry));
    }

    /// <summary>
    /// Fires an async audit write from a synchronous event handler,
    /// catching and swallowing exceptions to avoid crashing the event bus.
    /// </summary>
    private static void RecordSafely(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch
            {
                // Audit is best-effort; never let failures propagate to the event bus.
            }
        });
    }
}
