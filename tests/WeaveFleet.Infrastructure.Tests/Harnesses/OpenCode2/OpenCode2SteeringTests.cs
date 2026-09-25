using System.Net;
using System.Text;
using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// V2 takes a prompt's delivery: <c>steer</c> goes into the running turn at its next step, <c>queue</c> waits for it to
/// end. Fleet says which every time the user sends, and marks a steered prompt in its metadata for history.
/// </summary>
public sealed class OpenCode2SteeringTests
{
    [Theory]
    [InlineData(PromptDelivery.Steer, "steer")]
    [InlineData(PromptDelivery.Queue, "queue")]
    [InlineData(null, null)]
    public void A_prompt_says_how_it_goes_in_when_Fleet_knows(PromptDelivery? delivery, string? expected)
        => OpenCode2HarnessSession.Delivery(delivery).ShouldBe(expected);

    [Fact]
    public async Task A_steered_prompt_goes_in_as_a_steer_with_Fleets_mark()
    {
        var body = await SentBodyAsync(OpenCode2Deliveries.Steer);

        body.GetProperty("delivery").GetString().ShouldBe("steer");
        body.GetProperty("metadata").GetProperty("fleetDelivery").GetString().ShouldBe("steer");
    }

    [Fact]
    public async Task A_queued_prompt_says_queue_and_carries_no_mark()
    {
        var body = await SentBodyAsync(OpenCode2Deliveries.Queue);

        body.GetProperty("delivery").GetString().ShouldBe("queue");
        body.TryGetProperty("metadata", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_prompt_that_doesnt_say_leaves_it_to_V2()
    {
        var body = await SentBodyAsync(delivery: null);

        body.TryGetProperty("delivery", out _).ShouldBeFalse();
        body.TryGetProperty("metadata", out _).ShouldBeFalse();
    }

    [Fact]
    public void History_shows_the_marked_prompt_as_steered()
    {
        // V2 keeps a prompt's metadata on its user message (recorded from 2.0.9).
        const string page = """
            {"data":[
              {"id":"msg_0d21017f9001UUdlt2Zuyk2AfQ","type":"assistant","time":{"created":1790230665200},"finish":"tool-calls","content":[]},
              {"id":"msg_fleetsteer0001aaaaaaaaaaa","type":"user","time":{"created":1790230671136},"text":"stop, wrong file","metadata":{"fleetDelivery":"steer"}},
              {"id":"msg_fleetqueue0001aaaaaaaaaaa","type":"user","time":{"created":1790230690000},"text":"after the turn"}
            ]}
            """;

        var messages = OpenCode2History.ToHarnessMessages(
            JsonSerializer.Deserialize(page, OpenCode2JsonContext.Default.OpenCode2MessagePage)!.Data!);

        messages.Select(m => (m.Id, m.Steered)).ShouldBe(
        [
            ("msg_0d21017f9001UUdlt2Zuyk2AfQ", false),
            ("msg_fleetsteer0001aaaaaaaaaaa", true),
            ("msg_fleetqueue0001aaaaaaaaaaa", false),
        ]);
    }

    [Fact]
    public void A_steer_still_in_the_inbox_shows_as_the_users_steered_message()
    {
        // The shape GET /api/session/{id}/inbox answers with while a steer waits for the turn's next step (2.0.9).
        const string inbox = """
            {"data":[
              {"id":"msg_1a0d2102f18000ZZZZZZZZZZZZZZ","sessionID":"ses_1","time":{"created":1790230671136},"type":"user",
               "payload":{"text":"stop, wrong file","metadata":{"fleetDelivery":"steer"}},"delivery":"steer"},
              {"id":"msg_notice","sessionID":"ses_1","time":{"created":1790230672000},"type":"synthetic",
               "payload":{"text":"a background task finished"},"delivery":"steer"}
            ]}
            """;

        var pending = OpenCode2History.PendingPrompts(
            JsonSerializer.Deserialize(inbox, OpenCode2JsonContext.Default.OpenCode2InboxPage)!.Data!);

        var message = pending.ShouldHaveSingleItem();
        message.Id.ShouldBe("msg_1a0d2102f18000ZZZZZZZZZZZZZZ");
        message.Role.ShouldBe("user");
        message.Steered.ShouldBeTrue();
        message.TextContent.ShouldBe("stop, wrong file");
    }

    private static async Task<JsonElement> SentBodyAsync(string? delivery)
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"id":"msg_1","type":"user"}}""", Encoding.UTF8, "application/json"),
        });
        using var client = OpenCode2Fixtures.ClientServing("", api);

        await client.PromptAsync("ses_1", "stop, wrong file", "msg_1", null, delivery, CancellationToken.None);

        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/api/session/ses_1/prompt");
        return JsonDocument.Parse(request.Body!).RootElement.Clone();
    }
}
