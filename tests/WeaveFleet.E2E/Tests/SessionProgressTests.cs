using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for session progress. The test harness sends Fleet's own <c>todos.reported</c> and
/// <c>files.written</c> events, not OpenCode's, which proves a harness needs nothing else to drive the rings,
/// the strip and the Progress tab.
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
            ConfigureScenario(builder => builder.WithPromptResponse(response => Turn(response, MakeHarnessEvent(
                EventTypes.TodosReported,
                new
                {
                    items = new object[]
                    {
                        new { content = "Write the migration", status = "completed", priority = "high" },
                        new { content = "Drop the indexes", status = "in_progress" },
                        new { content = "Start the app on a fresh database", status = "pending" },
                    },
                }))));

            var sessionId = await CreateSessionAsync("Session progress");
            await new SessionDetailPage(Page).SendPromptAsync("Drop the dead tables", 30_000);

            // The row shows the ring and the count, pushed on the "sessions" topic. The number shows only while the
            // session works; once the turn ends an unfinished list keeps the ring, and the count stays in its tooltip.
            var row = new FleetSidebarPage(Page).GetSessionLeaf(sessionId);
            await Assertions.Expect(row.Locator(".session-progress")).ToHaveAttributeAsync("title", new Regex("^1 of 3 done"), new() { Timeout = 15_000 });
            await Assertions.Expect(row.Locator(".progress-ring")).ToBeVisibleAsync();

            // The open session's strip shows the todo being worked on.
            await Assertions.Expect(Page.Locator(".progress-strip__current")).ToHaveTextAsync("Drop the indexes", new() { Timeout = 15_000 });
            await Assertions.Expect(Page.Locator(".progress-strip__count")).ToHaveTextAsync("1/3");

            // Both come back after a reload: the list and GET /progress read what the server stored.
            await Page.ReloadAsync();
            await Assertions.Expect(new FleetSidebarPage(Page).GetSessionLeaf(sessionId).Locator(".session-progress"))
                .ToHaveAttributeAsync("title", new Regex("^1 of 3 done"), new() { Timeout = 15_000 });
            await Assertions.Expect(Page.Locator(".progress-strip__count")).ToHaveTextAsync("1/3", new() { Timeout = 15_000 });
        });
    }

    [Fact]
    public async Task A_plan_file_the_agent_writes_and_ticks_shows_in_the_progress_tab()
    {
        await WithFailureCapture(async () =>
        {
            var planDirectory = Path.Combine(Path.GetTempPath(), $"fleet-e2e-plan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(planDirectory);
            var planPath = Path.Combine(planDirectory, "plan.md");
            const string plan = """
                # Drop the dead tables

                ### Phase 1: Migrate
                - [ ] 1. Write the migration
                - [ ] 2. Drop the indexes

                ### Phase 2: Check
                - [ ] 3. Start the app on a fresh database
                """;
            await File.WriteAllTextAsync(planPath, plan);

            try
            {
                // Two turns, each reporting that the agent wrote the plan file; the second also brings a todo list.
                var written = MakeHarnessEvent(EventTypes.FilesWritten, new { messageId = "msg-plan", paths = new[] { planPath } });
                var todos = MakeHarnessEvent(EventTypes.TodosReported, new
                {
                    items = new object[]
                    {
                        new { content = "Find every index on the dead tables", status = "completed" },
                        new { content = "Drop them in the migration", status = "in_progress" },
                    },
                });
                ConfigureScenario(builder => builder
                    .WithPromptResponse(response => Turn(response, written))
                    .WithPromptResponse(response => Turn(response, written, todos)));

                var sessionId = await CreateSessionAsync("Plan progress");
                var detail = new SessionDetailPage(Page);
                var row = new FleetSidebarPage(Page).GetSessionLeaf(sessionId);

                // Written: a plan at 0 of 3, with the Progress tab added and the strip naming the next step.
                await detail.SendPromptAsync("Plan it", 30_000);
                await Assertions.Expect(row.Locator(".session-progress")).ToHaveAttributeAsync("title", new Regex("^0 of 3 done"), new() { Timeout = 15_000 });
                await Assertions.Expect(Page.Locator(".progress-strip__current")).ToHaveTextAsync("Next: 1. Write the migration", new() { Timeout = 15_000 });

                // Ticked: 1 of 3.
                await File.WriteAllTextAsync(planPath, plan.Replace("- [ ] 1.", "- [x] 1.", StringComparison.Ordinal));
                await detail.SendPromptAsync("Tick it", 30_000);
                await Assertions.Expect(row.Locator(".session-progress")).ToHaveAttributeAsync("title", new Regex("^1 of 3 done"), new() { Timeout = 15_000 });

                // The Progress tab shows the phases, the tick, and the next step.
                await Page.GetByRole(AriaRole.Tab, new() { Name = "Progress" }).ClickAsync();
                await Assertions.Expect(Page.Locator(".progress-canvas__title")).ToHaveTextAsync("Drop the dead tables");
                await Assertions.Expect(Page.Locator(".progress-group__title")).ToHaveTextAsync(["Phase 1: Migrate", "Phase 2: Check"]);
                await Assertions.Expect(Page.Locator(".progress-step--ticked")).ToContainTextAsync("Write the migration");
                await Assertions.Expect(Page.Locator(".progress-step--current")).ToContainTextAsync("Drop the indexes");
                await Assertions.Expect(Page.Locator(".progress-step--current .progress-todo")).ToHaveTextAsync(
                    ["Find every index on the dead tables", "Drop them in the migration"]);
                await Assertions.Expect(Page.Locator(".progress-strip")).ToHaveCountAsync(0);
            }
            finally
            {
                try { Directory.Delete(planDirectory, recursive: true); } catch { /* best effort */ }
            }
        });
    }

    private async Task<string> CreateSessionAsync(string title)
    {
        var dashboard = new FleetDashboardPage(Page);
        await dashboard.GotoAsync();

        var dialog = await dashboard.ClickNewSessionAsync();
        await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        await dialog.SetTitleAsync(title);

        var detail = await dialog.SubmitAsync();
        await detail.WaitForLoadedAsync();
        return new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
    }

    /// <summary>A turn: busy, the given events, then idle.</summary>
    private static PromptResponseBuilder Turn(PromptResponseBuilder response, params HarnessEvent[] events)
    {
        response.AddEvent(MakeHarnessEvent(EventTypes.SessionStatus, new { sessionId = "_placeholder_", status = new { type = "busy" } }));
        foreach (var evt in events)
            response.AddEvent(evt, TimeSpan.FromMilliseconds(100));
        return response
            .AddEvent(MakeHarnessEvent(EventTypes.SessionStatus, new { sessionId = "_placeholder_", status = new { type = "idle" } }), TimeSpan.FromMilliseconds(100))
            .AddEvent(MakeHarnessEvent(EventTypes.SessionIdle, new { sessionId = "_placeholder_" }), TimeSpan.FromMilliseconds(50));
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
