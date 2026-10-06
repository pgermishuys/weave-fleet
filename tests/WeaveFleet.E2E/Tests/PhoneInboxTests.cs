using WeaveFleet.Domain.Harnesses;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>The phone's Needs-you inbox: an agent's permission ask shows up and Allow once answers it.</summary>
[Trait("Category", "E2E")]
public sealed class PhoneInboxTests(PhoneFleetWebApplicationFactory factory, PlaywrightFixture playwright)
    : PhoneE2ETestBase(factory, playwright), IClassFixture<PhoneFleetWebApplicationFactory>, IClassFixture<PlaywrightFixture>
{
    [Fact]
    public async Task A_permission_ask_shows_in_the_inbox_and_Allow_once_answers_it()
    {
        var phone = await PhoneAsync();
        await PairAsync(phone);
        var (sessionId, _, harness) = await StartSessionAsync("Fix flaky SignalR reconnect test");
        await StartTurnAsync(harness, sessionId, "I raised the reconnect timeout to 5 s.");
        await harness.AskPermissionAsync(new PermissionAsk
        {
            Id = "perm-e2e-1",
            SessionId = sessionId,
            Kind = PermissionKinds.Shell,
            Tool = "bash",
            Title = "dotnet test tests/WeaveFleet.IntegrationTests",
            Always = ["dotnet test *"],
            AskedAt = DateTimeOffset.UtcNow,
        });

        await phone.GotoAsync("/phone");
        var card = phone.GetByTestId("inbox-ask").Filter(new LocatorFilterOptions { HasText = "Fix flaky SignalR reconnect test" });
        await card.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await card.GetByText("dotnet test tests/WeaveFleet.IntegrationTests").WaitForAsync();

        await card.GetByTestId("inbox-allow-once").ClickAsync();

        await EventuallyAsync(() => harness.PermissionReplies.Any(r => r.RequestId == "perm-e2e-1" && r.Reply == "once"));
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
    }

    [Fact]
    public async Task The_service_worker_answer_code_answers_with_the_device_token()
    {
        // Playwright can't press a system notification's button, so this runs the code the service worker runs for
        // Allow once, from the page, with the phone's own token.
        var phone = await PhoneAsync();
        await PairAsync(phone);
        var (sessionId, _, harness) = await StartSessionAsync("Answered from a notification");
        await StartTurnAsync(harness, sessionId, "Running the tests.");
        await harness.AskPermissionAsync(new PermissionAsk
        {
            Id = "perm-e2e-2",
            SessionId = sessionId,
            Kind = PermissionKinds.Shell,
            Tool = "bash",
            Title = "dotnet test",
            AskedAt = DateTimeOffset.UtcNow,
        });

        var status = await phone.EvaluateAsync<int>("""
            async ([sessionId]) => {
              const credentials = JSON.parse(localStorage.getItem("weave:device-credentials"));
              const response = await fetch(`/api/sessions/${sessionId}/permissions/perm-e2e-2`, {
                method: "POST",
                headers: { "Content-Type": "application/json", Authorization: `Bearer ${credentials.token}` },
                credentials: "omit",
                body: JSON.stringify({ reply: "once" }),
              });
              return response.status;
            }
            """, new object[] { sessionId });

        status.ShouldBe(204);
        await EventuallyAsync(() => harness.PermissionReplies.Any(r => r.RequestId == "perm-e2e-2"));
    }
}
