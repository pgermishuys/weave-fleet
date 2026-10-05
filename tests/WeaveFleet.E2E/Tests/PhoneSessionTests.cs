using System.Net.Http.Json;
using System.Text.Json;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>A session on the phone: queueing while the agent works, Stop, and answering a question docked above the composer.</summary>
[Trait("Category", "E2E")]
public sealed class PhoneSessionTests(PhoneFleetWebApplicationFactory factory, PlaywrightFixture playwright)
    : PhoneE2ETestBase(factory, playwright), IClassFixture<PhoneFleetWebApplicationFactory>, IClassFixture<PlaywrightFixture>
{
    [Fact]
    public async Task While_the_agent_works_a_message_queues_and_Stop_stops_it()
    {
        var phone = await PhoneAsync();
        await PairAsync(phone);
        var (sessionId, _, harness) = await StartSessionAsync("Composer references refactor");
        await StartTurnAsync(harness, sessionId, "Editing ReferencePicker.vue.");

        await phone.GotoAsync($"/phone/s/{MachineId}/{sessionId}");
        await phone.GetByTestId("phone-session-state").Filter(new LocatorFilterOptions { HasText = "Working" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await phone.GetByTestId("composer-stop").WaitForAsync();

        await phone.GetByTestId("phone-composer-input").FillAsync("Skip the E2E run, just open the PR");
        await phone.GetByTestId("composer-queue").ClickAsync();
        await phone.GetByTestId("phone-queued").Filter(new LocatorFilterOptions { HasText = "Skip the E2E run" }).WaitForAsync();

        await phone.GetByTestId("composer-stop").ClickAsync();
        await phone.GetByTestId("phone-session-state").Filter(new LocatorFilterOptions { HasText = "Working" }).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
    }

    [Fact]
    public async Task A_question_docks_above_the_composer_and_is_answered()
    {
        var phone = await PhoneAsync();
        await PairAsync(phone);
        var (sessionId, _, harness) = await StartSessionAsync("Per-device tokens for machines");
        await StartTurnAsync(harness, sessionId, "One thing before I go on.");

        var questionInput = new
        {
            questions = new[]
            {
                new
                {
                    header = "Removed phone",
                    question = "When a removed phone calls in, should it get a plain 401 or a page?",
                    options = new[] { new { label = "Plain 401", description = "" }, new { label = "Explain", description = "" } },
                    multiple = false,
                    custom = false,
                },
            },
        };
        var messageId = $"msg-q-{Guid.NewGuid():N}";
        await harness.PushEventAsync(Event(harness, sessionId, "message.updated", new
        {
            info = new { id = messageId, sessionID = harness.InstanceId, role = "assistant", time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
        }));
        await harness.PushEventAsync(Event(harness, sessionId, "message.part.updated", new
        {
            sessionID = harness.InstanceId,
            part = new { type = "tool", id = "call-q-phone", callID = "call-q-phone", tool = "question", sessionID = harness.InstanceId, messageID = messageId, state = new { status = "running", input = questionInput } },
        }));
        harness.SetQuestionContext(messageId, JsonSerializer.SerializeToElement(questionInput));

        await phone.GotoAsync($"/phone/s/{MachineId}/{sessionId}");
        var dock = phone.GetByTestId("docked-question");
        await dock.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // Compact, above the composer, which stays.
        await phone.GetByTestId("phone-composer").WaitForAsync();

        // One of the first options answers with a tap.
        await dock.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Plain 401" }).ClickAsync();

        await EventuallyAsync(() => harness.LastAnswers is { Count: 1 } answers && answers[0].Contains("Plain 401"));

        // The test harness's own completion event carries no Fleet session id (as in QuestionToolTests): push one that does.
        await harness.PushEventAsync(Event(harness, sessionId, "message.part.updated", new
        {
            sessionID = harness.InstanceId,
            part = new
            {
                type = "tool", id = "call-q-phone", callID = "call-q-phone", tool = "question", sessionID = harness.InstanceId, messageID = messageId,
                state = new { status = "completed", input = questionInput, metadata = new { answers = harness.LastAnswers } },
            },
        }));
        await dock.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
    }

    [Fact]
    public async Task A_new_session_starts_from_the_phone_and_opens()
    {
        var phone = await PhoneAsync();
        await PairAsync(phone);

        await phone.GotoAsync("/phone");
        await phone.GetByTestId("phone-new-session-button").ClickAsync();
        await phone.GetByTestId("phone-new-message").FillAsync("Find out why the reconnect test is flaky");
        await phone.GetByTestId("phone-new-folder").ClickAsync();
        await phone.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "No folder" }).ClickAsync();
        await phone.GetByTestId("phone-new-start").ClickAsync();

        await phone.WaitForURLAsync(new System.Text.RegularExpressions.Regex($"/phone/s/{MachineId}/[^/?]+$"), new PageWaitForURLOptions { Timeout = 15_000 });
        await phone.GetByTestId("phone-session-header").Filter(new LocatorFilterOptions { HasText = "Find out why the reconnect test is flaky" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var sessionId = phone.Url.Split('/').Last();
        using var owner = OwnerClient();
        var session = await owner.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
        session.GetRawText().ShouldContain("Find out why the reconnect test is flaky");
    }
}
