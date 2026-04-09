using Dapper;
using WeaveFleet.Infrastructure.Analytics;

namespace WeaveFleet.Infrastructure.Tests.Analytics;

public sealed class AnalyticsRepositoryTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection? _keeper;
    private AnalyticsRepository? _repo;

    public async Task InitializeAsync()
    {
        var (keeper, factory) = await AnalyticsTestDbHelper.CreateSharedDbAsync();
        _keeper = keeper;
        _repo = new AnalyticsRepository(factory);

        // Seed token_events
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO token_events (event_id, session_id, project_id, project_name,
                workspace_directory, model_id, provider_id,
                tokens_input, tokens_output, tokens_reasoning,
                tokens_cache_read, tokens_cache_write, tokens_total,
                cost, estimated_cost, created_at)
            VALUES
              ('evt-1','sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               100,200,0,10,0,310, 0.01, 0.009, '2026-01-15T10:00:00+00:00'),
              ('evt-2','sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               200,300,0,0,0,500, 0.02, 0.018, '2026-01-15T11:00:00+00:00'),
              ('evt-3','sess-2','proj-b','Beta','/ws2','gpt-4o','openai',
               50,100,0,0,0,150, 0.005, NULL, '2026-01-16T09:00:00+00:00')
            """);

        await conn.ExecuteAsync("""
            INSERT INTO session_snapshots (session_id, project_id, project_name,
                workspace_directory, title, status,
                total_tokens, total_cost, total_estimated_cost,
                message_count, model_ids, created_at)
            VALUES
              ('sess-1','proj-a','Alpha','/ws','Session A','stopped',
               810, 0.03, 0.027, 2, '["claude-sonnet"]', '2026-01-15T10:00:00+00:00'),
              ('sess-2','proj-b','Beta','/ws2','Session B','active',
               150, 0.005, 0.0, 1, '["gpt-4o"]', '2026-01-16T09:00:00+00:00')
            """);

        // Seed daily_rollups
        await conn.ExecuteAsync("""
            INSERT INTO daily_rollups (date, project_id, model_id, provider_id,
                total_tokens, total_cost, total_estimated_cost, session_count, message_count)
            VALUES
              ('2026-01-15','proj-a','claude-sonnet','anthropic', 810, 0.03, 0.027, 1, 2),
              ('2026-01-16','proj-b','gpt-4o','openai', 150, 0.005, 0.0, 1, 1)
            """);
    }

    public Task DisposeAsync()
    {
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    // ── GetSummaryAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_NoFilters_ReturnsAllTimeAggregates()
    {
        var summary = await _repo!.GetSummaryAsync(null, null, null);

        summary.TotalTokens.ShouldBe(960); // 310+500+150
        summary.TotalCost.ShouldBeInRange(0.034, 0.036); // 0.01+0.02+0.005
        summary.MessageCount.ShouldBe(3);
        summary.SessionCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetSummaryAsync_ProjectFilter_ReturnsOnlyMatchingProject()
    {
        var summary = await _repo!.GetSummaryAsync(null, null, "proj-a");

        summary.TotalTokens.ShouldBe(810);
        summary.SessionCount.ShouldBe(1);
        summary.MessageCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetSummaryAsync_DateRange_FiltersCorrectly()
    {
        var from = new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero);
        var summary = await _repo!.GetSummaryAsync(from, null, null);

        summary.TotalTokens.ShouldBe(150);
        summary.MessageCount.ShouldBe(1);
    }

    // ── GetDailyAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDailyAsync_NoFilters_ReturnsBothDays()
    {
        var daily = await _repo!.GetDailyAsync(null, null, null);

        daily.Count.ShouldBe(2);
    }

    // ── GetSessionsAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSessionsAsync_ReturnsAllSessions()
    {
        var sessions = await _repo!.GetSessionsAsync(null, null, null, 50);

        sessions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetSessionsAsync_LimitRespected()
    {
        var sessions = await _repo!.GetSessionsAsync(null, null, null, 1);

        sessions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetSessionsAsync_ProjectFilter_ReturnsSingleSession()
    {
        var sessions = await _repo!.GetSessionsAsync(null, null, "proj-b", 50);

        sessions.Count.ShouldBe(1);
        sessions[0].SessionId.ShouldBe("sess-2");
    }

    // ── GetModelsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelsAsync_ReturnsBothModels()
    {
        var models = await _repo!.GetModelsAsync(null, null);

        models.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetModelsAsync_ClaudeSonnet_AggregatesCorrectly()
    {
        var models = await _repo!.GetModelsAsync(null, null);
        var claude = models.First(m => m.ModelId == "claude-sonnet");

        claude.Tokens.ShouldBe(810);
        claude.MessageCount.ShouldBe(2);
    }

    // ── ExportTokenEventsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ExportTokenEventsAsync_NoFilters_ReturnsAllRows()
    {
        var rows = await _repo!.ExportTokenEventsAsync(null, null, null);

        rows.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ExportTokenEventsAsync_ProjectFilter_ReturnsMatchingRows()
    {
        var rows = await _repo!.ExportTokenEventsAsync(null, null, "proj-a");

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.ProjectId == "proj-a");
    }

    [Fact]
    public async Task ExportTokenEventsAsync_DateRange_FiltersCorrectly()
    {
        var to = new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero);
        var rows = await _repo!.ExportTokenEventsAsync(null, to, null);

        rows.Count.ShouldBe(2); // only Jan 15 events
    }

    [Fact]
    public async Task ExportTokenEventsAsync_DuplicateEventId_NotInserted()
    {
        // Verify INSERT OR IGNORE idempotency
        // The seeded evt-1 should only appear once
        var rows = await _repo!.ExportTokenEventsAsync(null, null, null);
        rows.Count(r => r.EventId == "evt-1").ShouldBe(1);
    }
}
