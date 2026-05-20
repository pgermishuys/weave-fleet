using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Opt-in smoke tests that exercise Fleet against real harness runtimes.
/// </summary>
[Trait("Category", "HarnessSmoke")]
[Trait("Lane", "Smoke")]
public sealed class HarnessSmokeTests : HarnessSmokeTestBase, IClassFixture<PlaywrightFixture>
{
    private const string Prompt = "Say hello";
    private const int SessionCreateTimeoutMs = 120_000;
    private const int LlmResponseTimeoutMs = 180_000;

    public HarnessSmokeTests(PlaywrightFixture playwright)
        : base(playwright) { }

    public static TheoryData<HarnessSmokeSpec> Harnesses => new()
    {
        new HarnessSmokeSpec(
            "opencode",
            "opencode.enabled",
            "OpenCode",
            ["claude-code.enabled", "nucode.enabled"])
    };

    [HarnessSmokeTheory]
    [MemberData(nameof(Harnesses))]
    public async Task should_create_session_and_receive_assistant_response(HarnessSmokeSpec spec)
    {
        await RunWithHarnessSmokeFactoryAsync(spec, async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(WorkingDirectory);

            // Do not select a harness here: the smoke factory enables the row harness,
            // disables the configured alternatives, and makes the row harness the default.
            var detail = await dialog.SubmitAsync(SessionCreateTimeoutMs);
            await detail.WaitForLoadedAsync();

            var sessionId = GetCurrentSessionId(Page.Url);
            await AssertCreatedWithHarnessAsync(sessionId, spec.HarnessType);

            await AssertNoErrorSurfaceAsync();

            await detail.SendPromptAsync(Prompt, 30_000);

            var userMessage = detail.GetMessagesByRole("user").Filter(new LocatorFilterOptions { HasText = Prompt }).Nth(0);
            await Assertions.Expect(userMessage).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

            var assistantMessages = detail.GetMessagesByRole("assistant");
            await Assertions.Expect(assistantMessages.Nth(0)).ToBeAttachedAsync(
                new LocatorAssertionsToBeAttachedOptions { Timeout = LlmResponseTimeoutMs });

            await detail.WaitForIdleAsync(LlmResponseTimeoutMs);

            await AssertNoErrorSurfaceAsync();

            var userMessageCount = await detail.GetMessagesByRole("user").CountAsync();
            var assistantMessageCount = await assistantMessages.CountAsync();

            userMessageCount.ShouldBeGreaterThanOrEqualTo(1, $"Expected at least one user message, got {userMessageCount}.");
            assistantMessageCount.ShouldBeGreaterThanOrEqualTo(1, $"Expected at least one assistant/agent response, got {assistantMessageCount}.");
            (await detail.GetStatusAsync()).ShouldBe("idle");
        });
    }

    private async Task AssertCreatedWithHarnessAsync(string sessionId, string harnessType)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var session = await httpClient.GetFromJsonAsync<SessionDetailResponse>($"/api/sessions/{Uri.EscapeDataString(sessionId)}");

        session.ShouldNotBeNull();
        session.HarnessType.ShouldBe(harnessType);
    }

    private async Task AssertNoErrorSurfaceAsync()
    {
        await Assertions.Expect(Page.GetByTestId("new-session-error")).ToHaveCountAsync(0);
        await Assertions.Expect(Page.GetByTestId("send-prompt-error")).ToHaveCountAsync(0);
        await Assertions.Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    private static string GetCurrentSessionId(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 2 && string.Equals(segments[0], "sessions", StringComparison.Ordinal))
            return Uri.UnescapeDataString(segments[1]);

        throw new InvalidOperationException($"Expected a session detail URL, got '{url}'.");
    }

    private sealed record SessionDetailResponse([property: JsonPropertyName("harnessType")] string? HarnessType);
}
