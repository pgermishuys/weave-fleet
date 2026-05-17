using NuCode.Audit;
using NuCode.Events;
using NuCode.Fakes;
using NuCode.Permissions;

namespace NuCode;

public sealed class AuditEventSubscriberTests : IDisposable
{
    private readonly NuCodeEventBus _eventBus;
    private readonly CapturingAuditService _auditService;
    private readonly AuditEventSubscriber _subscriber;

    public AuditEventSubscriberTests()
    {
        _eventBus = new NuCodeEventBus();
        _auditService = new CapturingAuditService();
        _subscriber = new AuditEventSubscriber(_auditService, _eventBus);
    }

    public void Dispose() => _subscriber.Dispose();

    [Fact]
    public async Task tool_completed_writes_audit_entry_with_status_completed()
    {
        var sessionId = SessionId.New();
        var messageId = MessageId.New();

        _eventBus.Publish(ToolEvents.Started,
            new ToolEvents.ToolStartedInfo(sessionId, messageId, "bash", "call-1"));

        await Task.Delay(10); // let fire-and-forget settle

        _eventBus.Publish(ToolEvents.Completed,
            new ToolEvents.ToolCompletedInfo(sessionId, messageId, "bash", "call-1", "Done"));

        await Task.Delay(20);

        var entry = _auditService.ToolEntries.ShouldHaveSingleItem();
        entry.SessionId.ShouldBe(sessionId.Value);
        entry.ToolName.ShouldBe("bash");
        entry.Status.ShouldBe("completed");
        entry.Detail.ShouldBe("Done");
        entry.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task tool_failed_writes_audit_entry_with_status_error()
    {
        var sessionId = SessionId.New();
        var messageId = MessageId.New();

        _eventBus.Publish(ToolEvents.Started,
            new ToolEvents.ToolStartedInfo(sessionId, messageId, "read", "call-2"));

        await Task.Delay(10);

        _eventBus.Publish(ToolEvents.Failed,
            new ToolEvents.ToolFailedInfo(sessionId, messageId, "read", "call-2", "File not found"));

        await Task.Delay(20);

        var entry = _auditService.ToolEntries.ShouldHaveSingleItem();
        entry.Status.ShouldBe("error");
        entry.Detail.ShouldBe("File not found");
    }

    [Fact]
    public async Task permission_asked_writes_permission_entry_with_decision_asked()
    {
        var sessionId = SessionId.New();

        _eventBus.Publish(PermissionEvents.Asked,
            new PermissionEvents.PermissionAskedInfo("req-1", sessionId, "bash", ["*.sh"]));

        await Task.Delay(20);

        var entry = _auditService.PermissionEntries.ShouldHaveSingleItem();
        entry.SessionId.ShouldBe(sessionId.Value);
        entry.Permission.ShouldBe("bash");
        entry.Decision.ShouldBe("asked");
        entry.Patterns.ShouldBe(["*.sh"]);
    }

    [Fact]
    public async Task permission_replied_writes_permission_entry_with_correct_decision()
    {
        var sessionId = SessionId.New();

        _eventBus.Publish(PermissionEvents.Replied,
            new PermissionEvents.PermissionRepliedInfo("req-1", sessionId, PermissionDecision.AlwaysAllow));

        await Task.Delay(20);

        var entry = _auditService.PermissionEntries.ShouldHaveSingleItem();
        entry.Decision.ShouldBe("always-allow");
    }

    [Fact]
    public void disposing_subscriber_stops_receiving_events()
    {
        _subscriber.Dispose();

        var sessionId = SessionId.New();
        var messageId = MessageId.New();

        _eventBus.Publish(ToolEvents.Completed,
            new ToolEvents.ToolCompletedInfo(sessionId, messageId, "bash", "call-x", null));

        _auditService.ToolEntries.ShouldBeEmpty();
    }

    // ── Helpers ──

    private sealed class CapturingAuditService : IAuditService
    {
        public List<AuditEntry> ToolEntries { get; } = [];
        public List<AuditPermissionEntry> PermissionEntries { get; } = [];

        public Task RecordToolInvocationAsync(AuditEntry entry)
        {
            ToolEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordPermissionDecisionAsync(AuditPermissionEntry entry)
        {
            PermissionEntries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
