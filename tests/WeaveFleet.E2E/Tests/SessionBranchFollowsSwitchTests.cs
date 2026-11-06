using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;
using WeaveFleet.TestHarness;

namespace WeaveFleet.E2E.Tests;

/// <summary>An agent's <c>git switch</c> in a session's folder moves the session's branch label when the turn ends.</summary>
[Trait("Category", "E2E")]
public sealed class SessionBranchFollowsSwitchTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SessionBranchFollowsSwitchTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task The_row_follows_a_branch_switch_made_in_the_folder_without_a_reload()
    {
        await WithFailureCapture(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"fleet-e2e-branch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                Git(folder, "init", "-q", "-b", "feature/one");
                Git(folder, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "first");

                ConfigureScenario(builder => builder.WithPromptResponse(response => response
                    .AddEvent(Status("busy"))
                    .AddEvent(Status("idle"), TimeSpan.FromMilliseconds(100))
                    .AddEvent(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "_placeholder_", Timestamp = DateTimeOffset.UtcNow,
                        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { sessionId = "_placeholder_" }) }, TimeSpan.FromMilliseconds(50))));

                var dashboard = new FleetDashboardPage(Page);
                await dashboard.GotoAsync();
                var dialog = await dashboard.ClickNewSessionAsync();
                await dialog.SetDirectoryAsync(folder);
                await dialog.SetTitleAsync("Branch switch");
                var detail = await dialog.SubmitAsync();
                await detail.WaitForLoadedAsync();
                var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

                // The session works in a worktree Fleet made, on a branch Fleet named.
                string worktree;
                using (var scope = ServerServices.CreateScope())
                {
                    var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>().GetByIdAsync(sessionId);
                    worktree = (await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().GetByIdAsync(session!.WorkspaceId))!.Directory;
                }

                var row = Page.GetByTestId("session-branch");
                await Assertions.Expect(row).ToContainTextAsync("fleet/session-", new() { Timeout = 15_000 });

                // The agent switches branch during its turn; nothing reloads the page.
                Git(worktree, "switch", "-q", "-c", "feature/two");
                await new SessionDetailPage(Page).SendPromptAsync("Switch branch", 30_000);

                await Assertions.Expect(row).ToContainTextAsync("feature/two", new() { Timeout = 15_000 });
                await Assertions.Expect(row).Not.ToContainTextAsync("fleet/session-");

                var shot = Environment.GetEnvironmentVariable("BRANCH_SHOT");
                if (!string.IsNullOrEmpty(shot))
                    await Page.ScreenshotAsync(new PageScreenshotOptions { Path = shot });
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
            }
        });
    }

    private static HarnessEvent Status(string type) => new()
    {
        Type = EventTypes.SessionStatus,
        SessionId = "_placeholder_",
        Timestamp = DateTimeOffset.UtcNow,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { sessionId = "_placeholder_", status = new { type } }),
    };

    private static void Git(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.WaitForExit();
    }
}
