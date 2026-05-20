using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;
using WeaveFleet.E2E.Pages;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Opt-in smoke tests that exercise Fleet against the real OpenCode harness runtime.
/// </summary>
[Trait("Category", "HarnessSmoke")]
[Trait("Lane", "Smoke")]
public sealed class OpenCodeSmokeTests : HarnessSmokeTestBase,
    IClassFixture<SmokeFleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    private const string Prompt = "Say hello";
    private const int SessionCreateTimeoutMs = 120_000;
    private const int LlmResponseTimeoutMs = 180_000;

    public OpenCodeSmokeTests(SmokeFleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [HarnessSmokeFact]
    public async Task should_create_opencode_session_and_receive_assistant_response()
    {
        await WithFailureCapture(async () =>
        {
            var dashboard = new FleetDashboardPage(Page);
            await dashboard.GotoAsync();

            var dialog = await dashboard.ClickNewSessionAsync();
            await dialog.SetDirectoryAsync(WorkingDirectory);

            // Do not select a harness here: SmokeFleetWebApplicationFactory enables OpenCode,
            // disables the other harnesses, and makes OpenCode the default.
            var detail = await dialog.SubmitAsync(SessionCreateTimeoutMs);
            await detail.WaitForLoadedAsync();

            var sessionId = GetCurrentSessionId(Page.Url);
            await AssertCreatedWithOpenCodeHarnessAsync(sessionId);

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

    private async Task AssertCreatedWithOpenCodeHarnessAsync(string sessionId)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var session = await httpClient.GetFromJsonAsync<SessionDetailResponse>($"/api/sessions/{Uri.EscapeDataString(sessionId)}");

        session.ShouldNotBeNull();
        session.HarnessType.ShouldBe("opencode");
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

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class HarnessSmokeFactAttribute : FactAttribute
{
    private const string SmokeEnvironmentVariable = "FLEET_HARNESS_SMOKE";

    public HarnessSmokeFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(SmokeEnvironmentVariable), "1", StringComparison.Ordinal))
            Skip = $"Harness smoke tests are opt-in. Set {SmokeEnvironmentVariable}=1 to run them.";
    }
}
