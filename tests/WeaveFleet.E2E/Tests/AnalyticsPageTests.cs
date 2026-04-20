using System.Globalization;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the Analytics page.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Workflow")]
public sealed class AnalyticsPageTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private const string LocalUserId = "local-user";
    private readonly FleetWebApplicationFactory _factory;

    public AnalyticsPageTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright)
    {
        _factory = factory;
    }

    /// <summary>
    /// Task 25: Analytics page renders seeded aggregate values.
    /// </summary>
    [Fact]
    public async Task AnalyticsPageLoadsWithSeededAggregates()
    {
        await WithFailureCapture(async () =>
        {
            var seed = CreateSeed();
            await SeedAnalyticsAsync(seed);

            var analytics = new AnalyticsPage(Page);
            await analytics.GotoAsync();

            // Page should load without a hard error
            var hasError = await analytics.HasErrorAsync();
            hasError.ShouldBeFalse("Analytics page showed an error state");

            // No JavaScript errors should have thrown (check page title is present)
            var title = await Page.TitleAsync();
            title.ShouldNotBeNull();

            var dailyTrendCard = Page.GetByTestId("analytics-overview-daily-trend");
            await Microsoft.Playwright.Assertions.Expect(dailyTrendCard).ToBeVisibleAsync();

            await Microsoft.Playwright.Assertions.Expect(GetOverviewStatValue("Tokens"))
                .ToHaveTextAsync(seed.ExpectedTokensText);
            await Microsoft.Playwright.Assertions.Expect(GetOverviewStatValue("Sessions"))
                .ToHaveTextAsync(seed.ExpectedSessionsText);
        });
    }

    private ILocator GetOverviewStatValue(string label)
        => Page.Locator(
            $"xpath=//section[@aria-label='Overview analytics']//p[normalize-space()='{label}']/following-sibling::div/p[1]");

    private async Task SeedAnalyticsAsync(AnalyticsSeed seed)
    {
        var analyticsDb = _factory.KestrelServices.GetRequiredService<IAnalyticsDbConnectionFactory>();
        using var connection = analyticsDb.CreateConnection();

        await connection.ExecuteAsync(
            """
            INSERT INTO token_events (
                event_id,
                session_id,
                project_id,
                project_name,
                workspace_directory,
                model_id,
                provider_id,
                tokens_input,
                tokens_output,
                tokens_reasoning,
                tokens_cache_read,
                tokens_cache_write,
                tokens_total,
                cost,
                estimated_cost,
                created_at,
                user_id)
            VALUES
                (@EventId1, @SessionId1, 'proj-alpha', 'Alpha', '/tmp/alpha', 'claude-sonnet', 'anthropic', 100, 200, 0, 10, 0, 310, 0.01, 0.009, @CreatedAt1, @UserId),
                (@EventId2, @SessionId1, 'proj-alpha', 'Alpha', '/tmp/alpha', 'claude-sonnet', 'anthropic', 200, 300, 0, 0, 0, 500, 0.02, 0.018, @CreatedAt2, @UserId),
                (@EventId3, @SessionId2, 'proj-beta', 'Beta', '/tmp/beta', 'gpt-4o', 'openai', 50, 100, 0, 0, 0, 150, 0.005, NULL, @CreatedAt3, @UserId)
            """,
            new
            {
                seed.EventId1,
                seed.EventId2,
                seed.EventId3,
                seed.SessionId1,
                seed.SessionId2,
                seed.CreatedAt1,
                seed.CreatedAt2,
                seed.CreatedAt3,
                UserId = LocalUserId,
            });

        await connection.ExecuteAsync(
            """
            INSERT INTO session_snapshots (
                session_id,
                parent_session_id,
                project_id,
                project_name,
                workspace_directory,
                title,
                status,
                total_tokens,
                total_cost,
                total_estimated_cost,
                message_count,
                model_ids,
                created_at,
                ended_at,
                duration_seconds,
                user_id)
            VALUES
                (@SessionId1, NULL, 'proj-alpha', 'Alpha', '/tmp/alpha', 'Seeded Session A', 'stopped', 810, 0.03, 0.027, 2, '["claude-sonnet"]', @CreatedAt1, @EndedAt1, 5400, @UserId),
                (@SessionId2, NULL, 'proj-beta', 'Beta', '/tmp/beta', 'Seeded Session B', 'active', 150, 0.005, 0.0, 1, '["gpt-4o"]', @CreatedAt3, NULL, NULL, @UserId)
            """,
            new
            {
                seed.SessionId1,
                seed.SessionId2,
                seed.CreatedAt1,
                seed.CreatedAt3,
                seed.EndedAt1,
                UserId = LocalUserId,
            });

        await connection.ExecuteAsync(
            """
            INSERT INTO daily_rollups (
                date,
                user_id,
                project_id,
                model_id,
                provider_id,
                total_tokens,
                total_cost,
                total_estimated_cost,
                session_count,
                message_count)
            VALUES
                (@RollupDate1, @UserId, 'proj-alpha', 'claude-sonnet', 'anthropic', 810, 0.03, 0.027, 1, 2),
                (@RollupDate2, @UserId, 'proj-beta', 'gpt-4o', 'openai', 150, 0.005, 0.0, 1, 1)
            """,
            new
            {
                seed.RollupDate1,
                seed.RollupDate2,
                UserId = LocalUserId,
            });
    }

    private static AnalyticsSeed CreateSeed()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var firstDay = DateTimeOffset.UtcNow.Date.AddDays(-2);
        var secondDay = DateTimeOffset.UtcNow.Date.AddDays(-1);

        return new AnalyticsSeed(
            $"analytics-session-a-{suffix}",
            $"analytics-session-b-{suffix}",
            $"analytics-event-1-{suffix}",
            $"analytics-event-2-{suffix}",
            $"analytics-event-3-{suffix}",
            firstDay.AddHours(10).ToString("O"),
            firstDay.AddHours(11).ToString("O"),
            secondDay.AddHours(9).ToString("O"),
            firstDay.AddHours(11.5).ToString("O"),
            firstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            secondDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "960",
            "2");
    }

    private sealed record AnalyticsSeed(
        string SessionId1,
        string SessionId2,
        string EventId1,
        string EventId2,
        string EventId3,
        string CreatedAt1,
        string CreatedAt2,
        string CreatedAt3,
        string EndedAt1,
        string RollupDate1,
        string RollupDate2,
        string ExpectedTokensText,
        string ExpectedSessionsText);
}
