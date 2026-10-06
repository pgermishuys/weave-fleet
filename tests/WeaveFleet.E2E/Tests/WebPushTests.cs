using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Real Web Push, end to end: a paired phone turns notifications on, an agent asks for permission, and the push arrives
/// through Google's push service and the service worker. Needs the network and a headed Chromium, so it only runs with
/// <c>FLEET_PUSH_E2E=1</c> (under <c>xvfb-run -a</c> on a machine without a display), never in the default E2E run:
/// <c>FLEET_PUSH_E2E=1 xvfb-run -a dotnet test tests/WeaveFleet.E2E --filter "Category=PushE2E"</c>.
/// </summary>
[Trait("Category", "PushE2E")]
public sealed class WebPushTests(PhoneFleetWebApplicationFactory factory, PlaywrightFixture playwright)
    : PhoneE2ETestBase(factory, playwright), IClassFixture<PhoneFleetWebApplicationFactory>, IClassFixture<PlaywrightFixture>
{
    [Fact]
    public async Task A_permission_ask_reaches_the_phone_as_a_push()
    {
        if (Environment.GetEnvironmentVariable("FLEET_PUSH_E2E") != "1")
            return;

        // Playwright's ordinary contexts are incognito, where Chrome has no Push API: use a real profile.
        using var playwrightInstance = await Microsoft.Playwright.Playwright.CreateAsync();
        var profile = Directory.CreateTempSubdirectory("fleet-push-profile-").FullName;
        await using var phoneContext = await playwrightInstance.Chromium.LaunchPersistentContextAsync(profile, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            BaseURL = ServerUrl,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            Permissions = ["notifications"],
        });
        var phone = phoneContext.Pages.Count > 0 ? phoneContext.Pages[0] : await phoneContext.NewPageAsync();
        phone.SetDefaultTimeout(30_000);

        await PairAsync(phone);
        await phone.GetByTestId("setup-turn-on").ClickAsync();
        // The first subscription in a fresh profile takes 20–90 s while Chrome registers with its push service.
        await phone.GetByTestId("setup-on").WaitForAsync(new LocatorWaitForOptions { Timeout = 150_000 });

        var (sessionId, _, harness) = await StartSessionAsync("Push from a permission ask");
        await StartTurnAsync(harness, sessionId, "Running the tests.");
        await harness.AskPermissionAsync(new PermissionAsk
        {
            Id = "perm-push-1",
            SessionId = sessionId,
            Kind = PermissionKinds.Shell,
            Tool = "bash",
            Title = "dotnet test",
            AskedAt = DateTimeOffset.UtcNow,
        });

        JsonElement[] shown = [];
        var until = DateTime.UtcNow.AddSeconds(90);
        while (shown.Length == 0 && DateTime.UtcNow < until)
        {
            var json = await phone.EvaluateAsync<string>("""
                async () => JSON.stringify((await (await navigator.serviceWorker.ready).getNotifications())
                  .map((n) => ({ title: n.title, body: n.body, tag: n.tag, data: n.data })))
                """);
            shown = [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(element => element.Clone())];
            if (shown.Length == 0)
                await Task.Delay(1000);
        }

        var notification = shown.ShouldHaveSingleItem();
        notification.GetProperty("title").GetString().ShouldBe("Push from a permission ask");
        notification.GetProperty("body").GetString()!.ShouldContain("Wants to run dotnet test");
        notification.GetProperty("tag").GetString().ShouldBe($"{MachineId}:{sessionId}");
        notification.GetProperty("data").GetProperty("url").GetString().ShouldBe($"/phone/s/{MachineId}/{sessionId}?ask=perm-push-1");
        notification.GetRawText().ShouldNotContain("fdt_");
    }
}
