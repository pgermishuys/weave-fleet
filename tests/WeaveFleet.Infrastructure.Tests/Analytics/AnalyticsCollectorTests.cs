using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Infrastructure.Analytics;

namespace WeaveFleet.Infrastructure.Tests.Analytics;

public sealed class AnalyticsCollectorTests
{
    private static TokenEventData MakeTokenEvent(string eventId = "evt-1") => new(
        EventId: eventId,
        SessionId: "sess-1",
        ProjectId: null,
        ProjectName: null,
        WorkspaceDirectory: "/tmp",
        ModelId: "claude-sonnet",
        ProviderId: "anthropic",
        TokensInput: 100,
        TokensOutput: 200,
        TokensReasoning: 0,
        TokensCacheRead: 0,
        TokensCacheWrite: 0,
        TokensTotal: 300,
        Cost: 0.005,
        EstimatedCost: null,
        CreatedAt: DateTimeOffset.UtcNow);

    private static SessionSnapshotData MakeSnapshot(string sessionId = "sess-1") => new(
        SessionId: sessionId,
        ParentSessionId: null,
        ProjectId: null,
        ProjectName: null,
        WorkspaceDirectory: "/tmp",
        Title: "Test",
        Status: "active",
        TotalTokens: 0,
        TotalCost: 0,
        TotalEstimatedCost: 0,
        MessageCount: 0,
        ModelIds: [],
        CreatedAt: DateTimeOffset.UtcNow,
        EndedAt: null,
        DurationSeconds: null);

    [Fact]
    public void AcceptTokenEvent_WritesToChannelReader()
    {
        var collector = new AnalyticsCollector(NullLogger<AnalyticsCollector>.Instance);
        var evt = MakeTokenEvent();

        collector.AcceptTokenEvent(evt);

        collector.Reader.TryRead(out var envelope).ShouldBeTrue();
        var tokenEnvelope = envelope.ShouldBeOfType<TokenEventEnvelope>();
        tokenEnvelope.Data.EventId.ShouldBe("evt-1");
    }

    [Fact]
    public void AcceptSessionSnapshot_WritesToChannelReader()
    {
        var collector = new AnalyticsCollector(NullLogger<AnalyticsCollector>.Instance);
        var snap = MakeSnapshot("sess-42");

        collector.AcceptSessionSnapshot(snap);

        collector.Reader.TryRead(out var envelope).ShouldBeTrue();
        var snapshotEnvelope = envelope.ShouldBeOfType<SessionSnapshotEnvelope>();
        snapshotEnvelope.Data.SessionId.ShouldBe("sess-42");
    }

    [Fact]
    public void AcceptTokenEvent_ExcessEvents_AreDropped_NotBlocking()
    {
        var collector = new AnalyticsCollector(NullLogger<AnalyticsCollector>.Instance);

        // Fill past capacity (10_000) — should never throw or block
        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 11_000; i++)
                collector.AcceptTokenEvent(MakeTokenEvent($"evt-{i}"));
        });

        exception.ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotThrow()
    {
        var collector = new AnalyticsCollector(NullLogger<AnalyticsCollector>.Instance);

        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => collector.AcceptTokenEvent(MakeTokenEvent($"evt-{i}"))));

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        exception.ShouldBeNull();
    }
}
