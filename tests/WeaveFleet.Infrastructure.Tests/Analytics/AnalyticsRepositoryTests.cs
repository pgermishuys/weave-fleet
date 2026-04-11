using Dapper;
using NSubstitute;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Analytics;

namespace WeaveFleet.Infrastructure.Tests.Analytics;

public sealed class AnalyticsRepositoryTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection? _keeper;
    private AnalyticsRepository? _repo;
    private AnalyticsTestDbHelper.SharedAnalyticsCacheFactory? _factory;

    public async Task InitializeAsync()
    {
        var (keeper, factory) = await AnalyticsTestDbHelper.CreateSharedDbAsync();
        _keeper = keeper;
        _factory = (AnalyticsTestDbHelper.SharedAnalyticsCacheFactory)factory;

        // All test data belongs to 'local-user'; repository queries as 'local-user'
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns("local-user");

        _repo = new AnalyticsRepository(factory, userContext);

        // Seed token_events
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO token_events (event_id, session_id, project_id, project_name,
                workspace_directory, model_id, provider_id,
                tokens_input, tokens_output, tokens_reasoning,
                tokens_cache_read, tokens_cache_write, tokens_total,
                cost, estimated_cost, created_at, user_id)
            VALUES
              ('evt-1','sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               100,200,0,10,0,310, 0.01, 0.009, '2026-01-15T10:00:00+00:00','local-user'),
              ('evt-2','sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               200,300,0,0,0,500, 0.02, 0.018, '2026-01-15T11:00:00+00:00','local-user'),
              ('evt-3','sess-2','proj-b','Beta','/ws2','gpt-4o','openai',
               50,100,0,0,0,150, 0.005, NULL, '2026-01-16T09:00:00+00:00','local-user')
            """);

        await conn.ExecuteAsync("""
            INSERT INTO session_snapshots (session_id, project_id, project_name,
                workspace_directory, title, status,
                total_tokens, total_cost, total_estimated_cost,
                message_count, model_ids, created_at, user_id)
            VALUES
              ('sess-1','proj-a','Alpha','/ws','Session A','stopped',
               810, 0.03, 0.027, 2, '["claude-sonnet"]', '2026-01-15T10:00:00+00:00','local-user'),
              ('sess-2','proj-b','Beta','/ws2','Session B','active',
               150, 0.005, 0.0, 1, '["gpt-4o"]', '2026-01-16T09:00:00+00:00','local-user')
            """);

        // Seed daily_rollups
        await conn.ExecuteAsync("""
            INSERT INTO daily_rollups (date, user_id, project_id, model_id, provider_id,
                total_tokens, total_cost, total_estimated_cost, session_count, message_count)
            VALUES
              ('2026-01-15','local-user','proj-a','claude-sonnet','anthropic', 810, 0.03, 0.027, 1, 2),
              ('2026-01-16','local-user','proj-b','gpt-4o','openai', 150, 0.005, 0.0, 1, 1)
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

// ── Tenant Isolation Tests ────────────────────────────────────────────────────

/// <summary>
/// Cross-user isolation tests: ensures that data seeded for user-b is never
/// returned when querying as user-a.
/// </summary>
public sealed class AnalyticsRepositoryTenantIsolationTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection? _keeper;
    private AnalyticsTestDbHelper.SharedAnalyticsCacheFactory? _factory;

    // Repositories scoped to different users
    private AnalyticsRepository? _repoUserA;
    private AnalyticsRepository? _repoUserB;

    public async Task InitializeAsync()
    {
        var (keeper, factory) = await AnalyticsTestDbHelper.CreateSharedDbAsync();
        _keeper = keeper;
        _factory = (AnalyticsTestDbHelper.SharedAnalyticsCacheFactory)factory;

        var userContextA = Substitute.For<IUserContext>();
        userContextA.UserId.Returns("user-a");

        var userContextB = Substitute.For<IUserContext>();
        userContextB.UserId.Returns("user-b");

        _repoUserA = new AnalyticsRepository(factory, userContextA);
        _repoUserB = new AnalyticsRepository(factory, userContextB);

        // Seed token_events for two users
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO token_events (event_id, session_id, project_id, project_name,
                workspace_directory, model_id, provider_id,
                tokens_input, tokens_output, tokens_reasoning,
                tokens_cache_read, tokens_cache_write, tokens_total,
                cost, estimated_cost, created_at, user_id)
            VALUES
              ('ua-evt-1','ua-sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               100,200,0,0,0,300, 0.01, 0.009, '2026-02-10T10:00:00+00:00','user-a'),
              ('ua-evt-2','ua-sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               50,100,0,0,0,150, 0.005, 0.004, '2026-02-10T11:00:00+00:00','user-a'),
              ('ub-evt-1','ub-sess-1','proj-b','Beta','/ws2','gpt-4o','openai',
               200,300,0,0,0,500, 0.02, 0.018, '2026-02-10T12:00:00+00:00','user-b')
            """);

        // Seed session_snapshots for two users
        await conn.ExecuteAsync("""
            INSERT INTO session_snapshots (session_id, project_id, project_name,
                workspace_directory, title, status,
                total_tokens, total_cost, total_estimated_cost,
                message_count, model_ids, created_at, user_id)
            VALUES
              ('ua-sess-1','proj-a','Alpha','/ws','Session A-1','active',
               450, 0.015, 0.013, 2, '["claude-sonnet"]', '2026-02-10T10:00:00+00:00','user-a'),
              ('ub-sess-1','proj-b','Beta','/ws2','Session B-1','active',
               500, 0.02, 0.018, 1, '["gpt-4o"]', '2026-02-10T12:00:00+00:00','user-b')
            """);

        // Seed daily_rollups for two users
        await conn.ExecuteAsync("""
            INSERT INTO daily_rollups (date, user_id, project_id, model_id, provider_id,
                total_tokens, total_cost, total_estimated_cost, session_count, message_count)
            VALUES
              ('2026-02-10','user-a','proj-a','claude-sonnet','anthropic', 450, 0.015, 0.013, 1, 2),
              ('2026-02-10','user-b','proj-b','gpt-4o','openai', 500, 0.02, 0.018, 1, 1)
            """);
    }

    public Task DisposeAsync()
    {
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsOnlyCurrentUsersData()
    {
        var summaryA = await _repoUserA!.GetSummaryAsync(null, null, null);
        var summaryB = await _repoUserB!.GetSummaryAsync(null, null, null);

        // user-a has 2 events totalling 450 tokens; user-b has 1 event with 500 tokens
        summaryA.TotalTokens.ShouldBe(450);
        summaryA.MessageCount.ShouldBe(2);
        summaryA.SessionCount.ShouldBe(1);

        summaryB.TotalTokens.ShouldBe(500);
        summaryB.MessageCount.ShouldBe(1);
        summaryB.SessionCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetDailyAsync_ReturnsOnlyCurrentUsersDailyRollups()
    {
        var dailyA = await _repoUserA!.GetDailyAsync(null, null, null);
        var dailyB = await _repoUserB!.GetDailyAsync(null, null, null);

        dailyA.Count.ShouldBe(1);
        dailyA[0].Tokens.ShouldBe(450);

        dailyB.Count.ShouldBe(1);
        dailyB[0].Tokens.ShouldBe(500);
    }

    [Fact]
    public async Task GetSessionsAsync_ReturnsOnlyCurrentUsersSessions()
    {
        var sessionsA = await _repoUserA!.GetSessionsAsync(null, null, null, 50);
        var sessionsB = await _repoUserB!.GetSessionsAsync(null, null, null, 50);

        sessionsA.Count.ShouldBe(1);
        sessionsA[0].SessionId.ShouldBe("ua-sess-1");

        sessionsB.Count.ShouldBe(1);
        sessionsB[0].SessionId.ShouldBe("ub-sess-1");
    }

    [Fact]
    public async Task GetSessionsAsync_DoesNotMixTokenTotalsAcrossUsersWithSameSessionId()
    {
        using var conn = _factory!.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO token_events (event_id, session_id, project_id, project_name,
                workspace_directory, model_id, provider_id,
                tokens_input, tokens_output, tokens_reasoning,
                tokens_cache_read, tokens_cache_write, tokens_total,
                cost, estimated_cost, created_at, user_id)
            VALUES
              ('ua-shared-evt','ua-sess-1','proj-a','Alpha','/ws','claude-sonnet','anthropic',
               10,20,0,0,0,30, 0.001, 0.001, '2026-02-11T10:00:00+00:00','user-a'),
              ('ub-shared-evt','ua-sess-1','proj-b','Beta','/ws2','gpt-4o','openai',
               100,200,0,0,0,300, 0.02, 0.018, '2026-02-11T11:00:00+00:00','user-b')
            """);

        var sessionsA = await _repoUserA!.GetSessionsAsync(null, null, null, 50);
        var sessionsB = await _repoUserB!.GetSessionsAsync(null, null, null, 50);

        sessionsA.Single(s => s.SessionId == "ua-sess-1").Tokens.ShouldBe(480);
        sessionsB.Single(s => s.SessionId == "ub-sess-1").Tokens.ShouldBe(500);
    }

    [Fact]
    public async Task GetModelsAsync_ReturnsOnlyCurrentUsersModelData()
    {
        var modelsA = await _repoUserA!.GetModelsAsync(null, null);
        var modelsB = await _repoUserB!.GetModelsAsync(null, null);

        // user-a uses claude-sonnet only
        modelsA.Count.ShouldBe(1);
        modelsA[0].ModelId.ShouldBe("claude-sonnet");

        // user-b uses gpt-4o only
        modelsB.Count.ShouldBe(1);
        modelsB[0].ModelId.ShouldBe("gpt-4o");
    }

    [Fact]
    public async Task ExportTokenEventsAsync_ReturnsOnlyCurrentUsersEvents()
    {
        var rowsA = await _repoUserA!.ExportTokenEventsAsync(null, null, null);
        var rowsB = await _repoUserB!.ExportTokenEventsAsync(null, null, null);

        rowsA.Count.ShouldBe(2);
        rowsA.ShouldAllBe(r => r.EventId!.StartsWith("ua-"));

        rowsB.Count.ShouldBe(1);
        rowsB.ShouldAllBe(r => r.EventId!.StartsWith("ub-"));
    }

    [Fact]
    public async Task GetSummaryAsync_WithProjectFilter_StillScopedByUser()
    {
        // user-a querying for proj-b (which belongs to user-b) should return empty
        var summary = await _repoUserA!.GetSummaryAsync(null, null, "proj-b");

        summary.TotalTokens.ShouldBe(0);
        summary.MessageCount.ShouldBe(0);
        summary.SessionCount.ShouldBe(0);
    }
}
