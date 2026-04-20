using System.Collections.Concurrent;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Smoke tests for primary application routes to ensure key pages load without client-side errors.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Smoke")]
public sealed class RouteSmokeTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public RouteSmokeTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Theory]
    [InlineData("/settings", "Settings")]
    [InlineData("/board", "Kanban Board")]
    [InlineData("/queue", "Task Queue")]
    [InlineData("/pipelines", "Pipelines")]
    [InlineData("/repositories", "Repositories")]
    [InlineData("/templates", "Templates")]
    [InlineData("/welcome", "Weave")]
    public async Task Route_LoadsWithoutClientErrors_AndRendersMainContent(string route, string expectedHeading)
    {
        await WithFailureCapture(async () =>
        {
            await ConfigureRouteDependenciesAsync(route);

            using var errorMonitor = CreateErrorMonitor();

            await Page.GotoAsync(route, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            Page.Url.ShouldContain(route);

            await Microsoft.Playwright.Assertions.Expect(Page.GetByRole(AriaRole.Main)).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(
                Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
                {
                    Name = expectedHeading,
                    Exact = true
                })).ToBeVisibleAsync();

            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            errorMonitor.AssertNoClientErrors(route);
        });
    }

    [Fact]
    public async Task LoginRoute_LoadsWithoutClientErrors_AndShowsAuthenticationActions()
    {
        await WithFailureCapture(async () =>
        {
            using var errorMonitor = CreateErrorMonitor();

            await Page.GotoAsync("/login", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var signInLink = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sign in", Exact = true });
            var signUpLink = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sign up", Exact = true });

            Page.Url.ShouldContain("/login");
            await Microsoft.Playwright.Assertions.Expect(Page.GetByRole(AriaRole.Main)).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(signInLink).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(signUpLink).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(Page.GetByText("Agent Fleet", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            errorMonitor.AssertNoClientErrors("/login");
        });
    }

    [Fact]
    public async Task PluginSettingsRoute_LoadsWithoutClientErrors_AndRendersGitHubConfigShell()
    {
        await WithFailureCapture(async () =>
        {
            await StubGitHubAuthStatusAsync();

            using var errorMonitor = CreateErrorMonitor();

            await Page.GotoAsync("/settings/plugins/github", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            Page.Url.ShouldContain("/settings/plugins/github");

            await Microsoft.Playwright.Assertions.Expect(Page.GetByRole(AriaRole.Main)).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(
                Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "GitHub", Exact = true })).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(Page.GetByText("GitHub integration", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Microsoft.Playwright.Assertions.Expect(
                Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Connect with GitHub", Exact = true })).ToBeVisibleAsync();

            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            errorMonitor.AssertNoClientErrors("/settings/plugins/github");
        });
    }

    private Task ConfigureRouteDependenciesAsync(string route)
        => route switch
        {
            "/settings" => StubSkillsEndpointAsync(),
            _ => Task.CompletedTask
        };

    private Task StubSkillsEndpointAsync()
        => Page.RouteAsync("**/api/skills", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = "{\"skills\":[]}"
            });
        });

    private Task StubGitHubAuthStatusAsync()
        => Page.RouteAsync("**/api/integrations/github/auth/status", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = "{\"connected\":false}"
            });
        });

    private ErrorMonitor CreateErrorMonitor()
        => new(Page);

    private static string FormatErrors(ConcurrentQueue<string> errors)
        => errors.IsEmpty ? "none" : string.Join(" | ", errors);

    private sealed class ErrorMonitor : IDisposable
    {
        private readonly IPage _page;
        private readonly ConcurrentQueue<string> _consoleErrors = [];
        private readonly ConcurrentQueue<string> _pageErrors = [];

        public ErrorMonitor(IPage page)
        {
            _page = page;
            _page.Console += OnConsole;
            _page.PageError += OnPageError;
        }

        public void Dispose()
        {
            _page.Console -= OnConsole;
            _page.PageError -= OnPageError;
        }

        public void AssertNoClientErrors(string route)
        {
            _pageErrors.IsEmpty.ShouldBeTrue($"Expected no page errors for route '{route}', but found: {FormatErrors(_pageErrors)}");
            _consoleErrors.IsEmpty.ShouldBeTrue($"Expected no console errors for route '{route}', but found: {FormatErrors(_consoleErrors)}");
        }

        private void OnConsole(object? _, IConsoleMessage message)
        {
            if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase) &&
                !message.Text.StartsWith("Failed to load resource:", StringComparison.OrdinalIgnoreCase))
            {
                _consoleErrors.Enqueue(message.Text);
            }
        }

        private void OnPageError(object? _, string message) => _pageErrors.Enqueue(message);
    }
}
