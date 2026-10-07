using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for dragging sessions in the sessions list. These need a real browser: Chrome cancels a drag whose row
/// moves during dragstart, which jsdom can't show (the Pinned group appearing at the top once did exactly that).
/// </summary>
[Trait("Category", "E2E")]
public sealed class SessionDragTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public SessionDragTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task A_session_dragged_onto_a_project_moves_into_it()
    {
        await WithFailureCapture(async () =>
        {
            var projectId = await CreateProjectAsync("Drag target");
            var sessionId = await CreateSessionAsync("Drag me into a project");
            await Page.ReloadAsync();

            var project = Page.Locator($".project-group[data-project-id='{projectId}']");
            await DragAsync(new FleetSidebarPage(Page).GetSessionLeaf(sessionId), project.Locator(".project-header"));

            await Assertions.Expect(project.Locator($"[data-session-id='{sessionId}']")).ToBeVisibleAsync(new() { Timeout = 10_000 });
            await Page.ReloadAsync();
            await Assertions.Expect(project.Locator($"[data-session-id='{sessionId}']")).ToBeVisibleAsync(new() { Timeout = 10_000 });
        });
    }

    [Fact]
    public async Task A_session_dragged_into_the_empty_pinned_group_is_pinned()
    {
        await WithFailureCapture(async () =>
        {
            var sessionId = await CreateSessionAsync("Drag me into Pinned");
            await Assertions.Expect(Page.GetByTestId("pinned-group")).ToHaveCountAsync(0);

            await DragAsync(new FleetSidebarPage(Page).GetSessionLeaf(sessionId), Page.GetByTestId("pinned-empty"));

            await Assertions.Expect(Page.GetByTestId("pinned-group").Locator($"[data-session-id='{sessionId}']"))
                .ToBeVisibleAsync(new() { Timeout = 10_000 });
        });
    }

    /// <summary>
    /// Drags with the mouse, in small steps like a person: the target is looked up once the drag has begun, since the
    /// list can change then (the empty Pinned group shows up). When Chrome cancels the drag, Playwright's next mouse
    /// call never returns, so each one has a time limit.
    /// </summary>
    private async Task DragAsync(ILocator source, ILocator target)
    {
        var from = await source.BoundingBoxAsync() ?? throw new InvalidOperationException("The row isn't on screen.");
        await Page.Mouse.MoveAsync(from.X + 40, from.Y + from.Height / 2);
        await Page.Mouse.DownAsync();
        await Step(Page.Mouse.MoveAsync(from.X + 45, from.Y + from.Height / 2 + 5, new() { Steps = 3 }));

        await Assertions.Expect(target).ToBeVisibleAsync();
        var to = await target.BoundingBoxAsync() ?? throw new InvalidOperationException("The drop target isn't on screen.");
        await Step(Page.Mouse.MoveAsync(to.X + 60, to.Y + to.Height / 2, new() { Steps = 10 }));
        await Step(Page.Mouse.UpAsync());

        static async Task Step(Task mouse)
        {
            try
            {
                await mouse.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The drag stalled: the browser most likely cancelled it as it began.");
            }
        }
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var response = await httpClient.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<JsonElement>();
        return project.GetProperty("id").GetString()!;
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
}
