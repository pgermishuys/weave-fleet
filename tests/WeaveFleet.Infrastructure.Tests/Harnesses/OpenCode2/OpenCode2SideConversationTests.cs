using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Side conversations (<c>/btw</c>) on OpenCode 2: <c>POST /api/session/{id}/fork</c> with <c>before</c> copies the
/// history before that message, so the fork is made at the last finished turn; Fleet's notes to the model go in as
/// synthetic messages that wait for the prompt (<c>resume: false</c>).
/// </summary>
public sealed class OpenCode2SideConversationTests
{
    private const string Session = "ses_main";

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (OpenCode2Server Server, OpenCode2HarnessSession Session) Start(StubHandler api)
    {
        var server = new OpenCode2Server("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);
        var session = new OpenCode2HarnessSession(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext("fleet-session-1", "local-user", "/work", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            delegations: null,
            NullLogger.Instance);
        return (server, session);
    }

    [Fact]
    public async Task Forks_before_a_turn_that_is_still_running()
    {
        // Newest first, as V2 pages: a step still streaming, one that stopped for tool calls, the prompt that started
        // them, then a finished turn.
        var api = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            $"/api/session/{Session}/message" => Json("""
                {"data":[
                  {"id":"msg_5","type":"assistant","time":{"created":5}},
                  {"id":"msg_4","type":"assistant","time":{"created":4,"completed":4},"finish":"tool-calls"},
                  {"id":"msg_3","type":"user","text":"slow please","time":{"created":3}},
                  {"id":"msg_2","type":"assistant","time":{"created":2,"completed":2},"finish":"stop"},
                  {"id":"msg_1","type":"user","text":"first","time":{"created":1}}],
                 "cursor":{"next":"c1"}}
                """),
            $"/api/session/{Session}/fork" => Json("""{"data":{"id":"ses_fork","parentID":"ses_main"}}"""),
            "/api/session/ses_fork/message" => Json("""{"data":[{"id":"msg_2c","type":"assistant","time":{"created":2,"completed":2},"finish":"stop"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var (server, session) = Start(api);
        await using (server)
        await using (session)
        {
            var fork = await session.ForkSideConversationAsync(CancellationToken.None);

            fork.ShouldBe(new SideConversationFork("ses_fork", "msg_2c"));
            var request = api.Requests.Single(r => r.Path == $"/api/session/{Session}/fork");
            JsonDocument.Parse(request.Body!).RootElement.GetProperty("before").GetString().ShouldBe("msg_3");
        }
    }

    [Fact]
    public async Task Copies_everything_when_the_last_turn_is_finished()
    {
        var api = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            $"/api/session/{Session}/message" => Json("""
                {"data":[{"id":"msg_2","type":"assistant","time":{"created":2,"completed":2},"finish":"stop"},
                         {"id":"msg_1","type":"user","text":"first","time":{"created":1}}],"cursor":{"next":"c1"}}
                """),
            $"/api/session/{Session}/fork" => Json("""{"data":{"id":"ses_fork"}}"""),
            "/api/session/ses_fork/message" => Json("""{"data":[]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var (server, session) = Start(api);
        await using (server)
        await using (session)
        {
            var fork = await session.ForkSideConversationAsync(CancellationToken.None);

            fork.ShouldBe(new SideConversationFork("ses_fork", null));
            var request = api.Requests.Single(r => r.Path == $"/api/session/{Session}/fork");
            JsonDocument.Parse(request.Body!).RootElement.TryGetProperty("before", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Notes_to_the_model_go_in_as_synthetic_messages_before_the_prompt()
    {
        var api = new StubHandler(_ => Json("""{"data":{}}"""));
        var (server, session) = Start(api);
        await using (server)
        await using (session)
        {
            await session.SendPromptAsync("what did I ask first?", new PromptOptions { MessageId = "msg_fleet", ModelNotes = ["reference only"] }, CancellationToken.None);

            api.Requests.Select(r => r.Path).ShouldBe([$"/api/session/{Session}/synthetic", $"/api/session/{Session}/prompt"]);
            var note = JsonDocument.Parse(api.Requests[0].Body!).RootElement;
            note.GetProperty("text").GetString().ShouldBe("reference only");
            note.GetProperty("resume").GetBoolean().ShouldBeFalse();
            JsonDocument.Parse(api.Requests[1].Body!).RootElement.GetProperty("text").GetString().ShouldBe("what did I ask first?");
        }
    }

    [Fact]
    public async Task A_prompt_without_notes_sends_none()
    {
        var api = new StubHandler(_ => Json("""{"data":{}}"""));
        var (server, session) = Start(api);
        await using (server)
        await using (session)
        {
            await session.SendPromptAsync("hello", null, CancellationToken.None);

            api.Requests.Select(r => r.Path).ShouldBe([$"/api/session/{Session}/prompt"]);
        }
    }
}
