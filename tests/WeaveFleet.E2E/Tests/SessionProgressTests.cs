using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E test for session progress. The test harness sends Fleet's own <c>todos.reported</c> event, not
/// OpenCode's, which proves a harness needs nothing else to drive the rings and the todo list.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SessionProgressTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SessionProgressTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task A_reported_todo_list_shows_on_the_row_and_in_the_open_session_and_survives_a_reload()
    {
        await WithFailureCapture(async () =>
        {
            ConfigureScenario(builder => builder.WithPromptResponse(response => response
                .AddEvent(MakeHarnessEvent(
                    EventTypes.SessionStatus,
                    new { sessionId = "_placeholder_", status = new { type = "busy" } }))
                .AddEvent(MakeHarnessEvent(
                    EventTypes.TodosReported,
                    new
                    {
                        items = new object[]
                        {
                            new { content = "Write the migration", status = "completed", priority = "high" },
                            new { content = "Drop the indexes", status = "in_progress" },
                            new { content = "Start the app on a fresh database", status = "pending" },
                        },
                    }),
                    TimeSpan.FromMilliseconds(100))
                .AddEvent(MakeHarnessEvent(
                    EventTypes.SessionStatus,
                    new { sessionId = "_placeholder_", status = new { type = "idle" } }),
                    TimeSpan.FromMilliseconds(100))
                .AddEvent(MakeHarnessEvent(
                    EventTypes.SessionIdle,
                    new { sessionId = "_placeholder_" }),
                    TimeSpan.FromMilliseconds(50))));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await dialog.SetTitleAsync("Session progress");

            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();
            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            await detail.SendPromptAsync("Drop the dead tables", 30_000);

            // The row shows the ring and the count, pushed on the "sessions" topic.
            var row = new FleetSidebarPage(Page).GetSessionLeaf(sessionId);
            await Assertions.Expect(row.Locator(".session-progress__count")).ToHaveTextAsync("1/3", new() { Timeout = 15_000 });
            await Assertions.Expect(row.Locator(".progress-ring")).ToBeVisibleAsync();

            // The open session shows the todo list from the server.
            await Assertions.Expect(Page.Locator(".meta-chip--todo")).ToHaveTextAsync("1 of 3 todos", new() { Timeout = 15_000 });

            // Both come back after a reload: the list and GET /progress read what the server stored.
            await Page.ReloadAsync();
            await Assertions.Expect(new FleetSidebarPage(Page).GetSessionLeaf(sessionId).Locator(".session-progress__count"))
                .ToHaveTextAsync("1/3", new() { Timeout = 15_000 });
            await Assertions.Expect(Page.Locator(".meta-chip--todo")).ToHaveTextAsync("1 of 3 todos", new() { Timeout = 15_000 });
        });
    }

    private static HarnessEvent MakeHarnessEvent(string type, object payload)
        => new()
        {
            Type = type,
            SessionId = "_placeholder_",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload),
        };
}
