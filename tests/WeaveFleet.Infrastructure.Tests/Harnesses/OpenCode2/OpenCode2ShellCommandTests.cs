using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Shell commands the user ran from the composer, from a recorded session (<c>user-shell.sse</c>, 2.0.9 against a
/// scripted model): <c>echo hello</c> and <c>ls /nope; exit 3</c> while the session was idle, a prompt ("slow please"),
/// <c>echo midturn</c> during its turn, then a second prompt. <c>user-shell.messages.json</c> is the session's history
/// afterwards. V2 keeps each command as a <c>shell</c> message and passes it to the model as a synthetic note.
/// </summary>
public sealed class OpenCode2ShellCommandTests
{
    private const string FleetSession = "fleet-session-1";
    private const string Session = "ses_f2df60c93ffeIgTLRri5VthO3E";
    private const string EchoHello = "msg_0d209fbf1001DPHOHqMfQxmaMX";
    private const string FailingCommand = "msg_0d20a03f5001Pq5xu37hOS9UHU";
    private const string MidTurnCommand = "msg_0d20a1b9f001WfC7nxvdC1G86q";

    [Fact]
    public async Task Each_command_is_a_message_of_the_users_own_holding_the_command()
    {
        var mapped = await MapAsync();

        var shells = mapped.Select(Translate).OfType<MessageUpdated>().Where(m => m.Payload.Info.Role == ShellCommands.Role).ToList();
        shells.Select(m => m.Payload.Info.Id).Distinct().ShouldBe([EchoHello, FailingCommand, MidTurnCommand]);

        var first = ToolParts(mapped).Where(p => p.MessageId == EchoHello).ToList();
        first[0].State.ShouldBeOfType<ToolRunningState>().Input!.Value.GetProperty("command").GetString().ShouldBe("echo hello");
        var ended = first[^1].State.ShouldBeOfType<ToolCompletedState>();
        ended.Output!.Value.GetString().ShouldBe("hello\n");
        ended.Metadata!.Value.GetProperty(ShellCommands.ExitMetadata).GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_command_carries_its_exit_code()
    {
        var parts = ToolParts(await MapAsync()).Where(p => p.MessageId == FailingCommand).ToList();

        var ended = parts[^1].State.ShouldBeOfType<ToolCompletedState>();
        ended.Output!.Value.GetString().ShouldBe("ls: cannot access '/nope': No such file or directory\n");
        ended.Metadata!.Value.GetProperty("exit").GetInt32().ShouldBe(3);
        ended.Metadata!.Value.GetProperty("status").GetString().ShouldBe("exited");
    }

    [Fact]
    public async Task V2s_note_passing_a_command_to_the_model_is_not_shown()
    {
        var mapped = await MapAsync();

        mapped.Select(Translate).OfType<MessageUpdated>().ShouldNotContain(m => m.Payload.Info.Role == OpenCode2Mapper.NoticeRole);
        mapped.Select(Translate).OfType<MessagePartUpdated>().Select(p => p.Payload.Part).OfType<TextMessageEventPart>()
            .ShouldNotContain(p => p.Text.StartsWith(OpenCode2Mapper.UserShellNoticePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_command_run_during_a_turn_leaves_the_turn_alone()
    {
        // The command's events come between the turn's; the turn still streams its reply and ends.
        var mapped = await MapAsync();

        mapped.Select(Translate).OfType<MessageUpdated>()
            .Where(m => m.Payload.Info.Role == "assistant")
            .Select(m => m.Payload.Info.Id)
            .Distinct()
            .Count()
            .ShouldBe(3);
        mapped.Count(e => e.Type == EventTypes.SessionIdle).ShouldBe(2);
    }

    [Fact]
    public void History_shows_the_same_commands_and_leaves_out_the_notes()
    {
        var history = History();

        var shells = history.Where(m => m.Role == ShellCommands.Role).ToList();
        shells.Select(m => m.Id).ShouldBe([EchoHello, FailingCommand, MidTurnCommand]);
        var failing = shells[1].Parts.OfType<ToolUsePart>().ShouldHaveSingleItem();
        failing.PartId.ShouldBe(OpenCode2Mapper.ToolPartId(FailingCommand, failing.ToolCallId));
        failing.State.ShouldBe(ToolUseState.Completed);
        failing.Arguments.GetProperty("command").GetString().ShouldBe("ls /nope; exit 3");
        failing.Metadata!.Value.GetProperty("exit").GetInt32().ShouldBe(3);

        history.ShouldNotContain(m => m.Role == OpenCode2Mapper.NoticeRole);
        history.Where(m => m.Role == "user").Select(m => m.TextContent).ShouldBe(["slow please", "what did you see"]);
    }

    [Fact]
    public async Task History_gives_a_command_the_part_id_it_had_live()
    {
        var live = ToolParts(await MapAsync()).Where(p => p.MessageId == MidTurnCommand).Select(p => p.Id).Distinct().ShouldHaveSingleItem();

        var fromHistory = History().Single(m => m.Id == MidTurnCommand).Parts.OfType<ToolUsePart>().ShouldHaveSingleItem();

        fromHistory.PartId.ShouldBe(live);
    }

    [Fact]
    public void A_command_caught_up_after_the_stream_was_down_reads_as_it_does_live()
    {
        var page = JsonSerializer.Deserialize(OpenCode2Fixtures.Read("user-shell.messages.json"), OpenCode2JsonContext.Default.OpenCode2MessagePage)!;
        var mapper = new OpenCode2Mapper(FleetSession);

        var events = page.Data!.Reverse().SelectMany(mapper.MapMessage).ToList();

        events.Select(Translate).OfType<MessageUpdated>().Where(m => m.Payload.Info.Role == ShellCommands.Role)
            .Select(m => m.Payload.Info.Id).ShouldBe([EchoHello, FailingCommand, MidTurnCommand]);
        events.Select(Translate).OfType<MessageUpdated>().ShouldNotContain(m => m.Payload.Info.Role == OpenCode2Mapper.NoticeRole);
    }

    [Fact]
    public void Work_the_agent_moved_into_the_background_still_shows_its_notice()
    {
        var mapped = new OpenCode2Mapper(FleetSession).Map(Event("session.inbox.enqueued", """
            {"inboxID":"msg_1","sessionID":"ses_1","item":{"type":"synthetic","payload":{
              "text":"<shell id=\"sh_1\" state=\"completed\" command=\"ls\">\nout\n</shell>",
              "metadata":{"source":"shell","shellID":"sh_1","state":"completed","exit":0} } } }
            """));

        mapped.Select(Translate).OfType<MessageUpdated>().ShouldHaveSingleItem().Payload.Info.Role.ShouldBe(OpenCode2Mapper.NoticeRole);
    }

    [Fact]
    public async Task Running_a_command_posts_it_under_the_id_fleet_gave_it()
    {
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var server = new OpenCode2Server("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);
        await using var session = new OpenCode2HarnessSession(
            "opencode2-test",
            new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext(FleetSession, "local-user", "/work", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            delegations: null,
            NullLogger.Instance);

        await session.RunShellCommandAsync(new ShellCommandOptions { Command = "git status", MessageId = "msg_fleet0001" }, CancellationToken.None);

        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe($"/api/session/{Session}/shell");
        var body = JsonDocument.Parse(request.Body!).RootElement;
        body.GetProperty("id").GetString().ShouldBe("msg_fleet0001");
        body.GetProperty("command").GetString().ShouldBe("git status");
    }

    private static IReadOnlyList<HarnessMessage> History()
    {
        var page = JsonSerializer.Deserialize(OpenCode2Fixtures.Read("user-shell.messages.json"), OpenCode2JsonContext.Default.OpenCode2MessagePage)!;
        return OpenCode2History.ToHarnessMessages(page.Data!.Reverse());
    }

    private static async Task<List<HarnessEvent>> MapAsync()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var mapped = new List<HarnessEvent>();
        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync("user-shell.sse"))
        {
            if (evt.SessionId == Session)
                mapped.AddRange(mapper.Map(evt));
        }

        return mapped;
    }

    private static List<ToolMessageEventPart> ToolParts(IEnumerable<HarnessEvent> events)
        => events
            .Where(e => e.Type == EventTypes.MessagePartUpdated)
            .Select(Translate)
            .OfType<MessagePartUpdated>()
            .Select(p => p.Payload.Part)
            .OfType<ToolMessageEventPart>()
            .ToList();

    private static DomainEvent? Translate(HarnessEvent evt)
        => new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance).Translate(evt with { FleetSessionId = FleetSession });

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_1",
        Created = 1_790_098_051_000,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };
}
