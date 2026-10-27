using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>GET /api/sessions/now answers the user's now lines, and its first call starts keeping them.</summary>
public sealed class SessionNowEndpointTests : IDisposable
{
    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_first_call_starts_the_bookkeeping_and_later_calls_answer_the_line_by_session()
    {
        var tracker = _factory.Services.GetRequiredService<SessionNowTracker>();
        tracker.IsActive.ShouldBeFalse();
        var client = _factory.CreateClient();

        var first = await client.GetAsync("/api/sessions/now");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>()).EnumerateObject().ShouldBeEmpty();
        tracker.IsActive.ShouldBeTrue();

        tracker.Observe("session-a", "local-user", new MessagePartUpdated
        {
            Payload = new MessagePartUpdatedPayload
            {
                SessionId = "session-a",
                Part = new ToolMessageEventPart
                {
                    Id = "p", SessionId = "session-a", MessageId = "m", ToolName = "bash", CallId = "c",
                    State = new ToolRunningState { Input = JsonDocument.Parse("""{"command":"pnpm test"}""").RootElement },
                },
            },
        });
        tracker.Observe("session-b", "someone-else", new MessagePartUpdated
        {
            Payload = new MessagePartUpdatedPayload
            {
                SessionId = "session-b",
                Part = new ReasoningMessageEventPart { Id = "p", SessionId = "session-b", MessageId = "m", Text = "x" },
            },
        });

        var second = await client.GetFromJsonAsync<JsonElement>("/api/sessions/now");

        var lines = second.EnumerateObject().ToList();
        lines.ShouldHaveSingleItem().Name.ShouldBe("session-a");
        lines[0].Value.GetProperty("text").GetString().ShouldBe("bash · pnpm test");
        lines[0].Value.GetProperty("kind").GetString().ShouldBe("tool");
    }
}
