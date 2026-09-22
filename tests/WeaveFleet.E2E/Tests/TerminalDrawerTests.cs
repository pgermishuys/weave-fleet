using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// The terminal drawer under the chat, with a real shell on the test server: open it with Ctrl J, run a command,
/// reload and see the output again, close the tab and see the terminal gone. Linux runs it in CI; macOS and
/// Windows are on the checklist in <c>.weave/plans/fleet-terminal-drawer.md</c>.
/// </summary>
[Trait("Category", "E2E")]
public sealed class TerminalDrawerTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public TerminalDrawerTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task Terminal_RunsACommand_KeepsItsOutputAcrossAReload_AndCloses()
    {
        if (!OperatingSystem.IsLinux()) return;

        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-terminal-1", "Terminal test response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();
            var sessionId = new Uri(Page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            // Ctrl J opens the drawer and starts a shell in the session's folder.
            await Page.Keyboard.PressAsync("Control+j");
            var screen = Page.Locator(".terminal-drawer .xterm-rows");
            await Assertions.Expect(screen).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(Page.Locator(".terminal-tab")).ToHaveCountAsync(1);

            await Page.Locator(".terminal-drawer .xterm").ClickAsync();
            await Page.Keyboard.TypeAsync("echo fleet-e2e-$((6*7))");
            await Page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(screen).ToContainTextAsync("fleet-e2e-42", new() { Timeout = 10_000 });

            // The drawer stays open across a reload, and the saved scrollback comes back.
            await Page.ReloadAsync();
            await Assertions.Expect(Page.Locator(".terminal-drawer .xterm-rows"))
                .ToContainTextAsync("fleet-e2e-42", new() { Timeout = 15_000 });

            // Closing the tab ends the shell: the drawer hides and the session has no terminals left.
            await Page.Locator(".terminal-tab__close").ClickAsync();
            await Assertions.Expect(Page.Locator(".terminal-tab")).ToHaveCountAsync(0);
            await WaitForNoTerminalsAsync(sessionId, TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task Terminal_KeysTypedInIt_DoNotOpenTheCommandPalette()
    {
        if (!OperatingSystem.IsLinux()) return;

        await WithFailureCapture(async () =>
        {
            ConfigureScenario(b =>
                b.WithSimpleTextResponse("_placeholder_", "msg-terminal-2", "Terminal keys response"));

            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();
            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            var detail = await dialog.SubmitAsync();
            await detail.WaitForLoadedAsync();

            await Page.Keyboard.PressAsync("Control+j");
            await Assertions.Expect(Page.Locator(".terminal-drawer .xterm-rows")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Page.Locator(".terminal-drawer .xterm").ClickAsync();
            await Assertions.Expect(Page.Locator("[data-testid=terminal-keyboard-hint]")).ToBeVisibleAsync();

            await Page.Keyboard.PressAsync("Control+k");
            await Page.Keyboard.PressAsync("Escape");

            await Assertions.Expect(Page.Locator("[data-slot=dialog-content]")).ToHaveCountAsync(0);

            // Ctrl J still belongs to Fleet: it hides the drawer from inside the terminal.
            await Page.Keyboard.PressAsync("Control+j");
            await Assertions.Expect(Page.Locator(".terminal-drawer")).ToBeHiddenAsync();
        });
    }

    /// <summary>
    /// Waits until the session has no terminals left on the server. The tab goes the moment it is clicked —
    /// the client drops it before it sends the DELETE — and the server only removes the terminal once the
    /// shell is dead, which waits for the read loop to drain. So asking once races the teardown.
    /// </summary>
    private async Task WaitForNoTerminalsAsync(string sessionId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var listed = -1;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await Page.EvaluateAsync<JsonElement>(
                "async (id) => (await fetch(`/api/sessions/${id}/terminals`, { credentials: 'include' })).json()",
                sessionId);
            listed = response.GetArrayLength();
            if (listed == 0) return;

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        listed.ShouldBe(0, $"The session still listed {listed} terminal(s) {timeout.TotalSeconds}s after the tab was closed.");
    }
}
