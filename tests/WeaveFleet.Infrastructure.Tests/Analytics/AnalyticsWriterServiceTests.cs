using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Analytics;

namespace WeaveFleet.Infrastructure.Tests.Analytics;

#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class AnalyticsWriterServiceTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private Microsoft.Data.Sqlite.SqliteConnection? _keeper;
    private AnalyticsTestDbHelper.SharedAnalyticsCacheFactory? _analyticsFactory;
    private AnalyticsCollector? _collector;
    private AnalyticsWriterService? _writer;

    private readonly ISessionRepository _sessionRepo = Substitute.For<ISessionRepository>();

    public async Task InitializeAsync()
    {
        var (keeper, factory) = await AnalyticsTestDbHelper.CreateSharedDbAsync();
        _keeper = keeper;
        _analyticsFactory = (AnalyticsTestDbHelper.SharedAnalyticsCacheFactory)factory;
        _collector = new AnalyticsCollector(NullLogger<AnalyticsCollector>.Instance);

        // Build a mock scope factory that returns the mock session repository
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeProvider.GetService(typeof(ISessionRepository)).Returns(_sessionRepo);
        scope.ServiceProvider.Returns(scopeProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var options = new FleetOptions
        {
            AnalyticsFlushIntervalSeconds = 1,
            AnalyticsMaxBatchSize = 50
        };

        _writer = new AnalyticsWriterService(
            _collector,
            _analyticsFactory,
            scopeFactory,
            options,
            NullLogger<AnalyticsWriterService>.Instance);
    }

    public Task DisposeAsync()
    {
        _keeper?.Dispose();
        _writer?.Dispose();
        return Task.CompletedTask;
    }

    private static TokenEventData MakeTokenEvent(string eventId = "evt-1",
        string sessionId = "sess-1") => new(
        EventId: eventId,
        SessionId: sessionId,
        ProjectId: "proj-a",
        ProjectName: "Alpha",
        WorkspaceDirectory: "/ws",
        ModelId: "claude-sonnet",
        ProviderId: "anthropic",
        TokensInput: 100,
        TokensOutput: 200,
        TokensReasoning: 0,
        TokensCacheRead: 0,
        TokensCacheWrite: 0,
        TokensTotal: 300,
        Cost: 0.01,
        EstimatedCost: 0.009,
        CreatedAt: DateTimeOffset.UtcNow);

    private static SessionSnapshotData MakeSnapshot(string sessionId = "sess-1") => new(
        SessionId: sessionId,
        ParentSessionId: null,
        ProjectId: "proj-a",
        ProjectName: "Alpha",
        WorkspaceDirectory: "/ws",
        Title: "Test Session",
        Status: "active",
        TotalTokens: 300,
        TotalCost: 0.01,
        TotalEstimatedCost: 0.009,
        MessageCount: 1,
        ModelIds: ["claude-sonnet"],
        CreatedAt: DateTimeOffset.UtcNow,
        EndedAt: null,
        DurationSeconds: null);

    [Fact]
    public async Task ProcessesTokenEvent_InsertsIntoTokenEventsTable()
    {
        _collector!.AcceptTokenEvent(MakeTokenEvent("evt-write-1"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _writer!.StartAsync(cts.Token);
        await Task.Delay(2500, cts.Token); // allow flush interval to fire
        await _writer.StopAsync(CancellationToken.None);

        using var conn = _analyticsFactory!.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM token_events WHERE event_id = 'evt-write-1'");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ProcessesSessionSnapshot_UpsertIntoSessionSnapshotsTable()
    {
        _collector!.AcceptSessionSnapshot(MakeSnapshot("sess-upsert-1"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _writer!.StartAsync(cts.Token);
        await Task.Delay(2500, cts.Token);
        await _writer.StopAsync(CancellationToken.None);

        using var conn = _analyticsFactory!.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM session_snapshots WHERE session_id = 'sess-upsert-1'");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DuplicateEventId_DoesNotCauseDuplicate()
    {
        _collector!.AcceptTokenEvent(MakeTokenEvent("evt-idem-1"));
        _collector.AcceptTokenEvent(MakeTokenEvent("evt-idem-1")); // duplicate

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _writer!.StartAsync(cts.Token);
        await Task.Delay(2500, cts.Token);
        await _writer.StopAsync(CancellationToken.None);

        using var conn = _analyticsFactory!.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM token_events WHERE event_id = 'evt-idem-1'");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task BatchFlush_MultipleEvents_AllPersisted()
    {
        for (int i = 0; i < 5; i++)
            _collector!.AcceptTokenEvent(MakeTokenEvent($"evt-batch-{i}", $"sess-{i}"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _writer!.StartAsync(cts.Token);
        await Task.Delay(2500, cts.Token);
        await _writer.StopAsync(CancellationToken.None);

        using var conn = _analyticsFactory!.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM token_events WHERE event_id LIKE 'evt-batch-%'");

        Assert.Equal(5, count);
    }
}
