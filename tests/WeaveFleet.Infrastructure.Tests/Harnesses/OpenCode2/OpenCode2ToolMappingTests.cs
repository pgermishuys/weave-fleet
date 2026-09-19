using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>Tool calls and questions from a recorded V2 session (<c>tools-questions.sse</c>), through the translator.</summary>
public sealed class OpenCode2ToolMappingTests
{
    private const string FleetSession = "fleet-session-1";

    [Fact]
    public async Task A_shell_call_shows_its_command_while_it_runs_and_its_output_when_done()
    {
        var parts = await ToolPartsAsync(OpenCode2Fixtures.ShellCall);

        parts.Select(p => p.State.GetType().Name).ShouldBe([nameof(ToolPendingState), nameof(ToolRunningState), nameof(ToolCompletedState)]);
        parts.ShouldAllBe(p => p.Id == OpenCode2Mapper.ToolPartId(OpenCode2Fixtures.ShellMessage, OpenCode2Fixtures.ShellCall)
            && p.ToolName == "shell"
            && p.CallId == OpenCode2Fixtures.ShellCall
            && p.MessageId == OpenCode2Fixtures.ShellMessage
            && p.SessionId == FleetSession);

        parts[1].State.ShouldBeOfType<ToolRunningState>().Input!.Value.GetProperty("command").GetString().ShouldBe("echo from-tool");
        var done = parts[2].State.ShouldBeOfType<ToolCompletedState>();
        done.Input!.Value.GetProperty("description").GetString().ShouldBe("Echo");
        done.Output!.Value.GetString().ShouldBe("from-tool\nCommand exited with code 0.");
        done.Metadata!.Value.GetProperty("exit").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_tool_says_why()
    {
        var parts = await ToolPartsAsync(OpenCode2Fixtures.FailedReadCall);

        parts[0].ToolName.ShouldBe("read");
        var failed = parts[^1].State.ShouldBeOfType<ToolErrorState>();
        failed.Error.ShouldBe("File not found: /nonexistent/missing.txt");
        failed.Input!.Value.GetProperty("path").GetString().ShouldBe("/nonexistent/missing.txt");
    }

    [Fact]
    public async Task A_question_shows_as_the_question_tool_with_its_questions_and_then_the_answer()
    {
        var parts = await ToolPartsAsync(OpenCode2Fixtures.AnsweredQuestionCall);

        // The client's question card reads the questions from the input and the answers from the metadata,
        // the same shapes as OpenCode's (1.x) question tool.
        parts[0].ToolName.ShouldBe("question");
        var asking = parts[1].State.ShouldBeOfType<ToolRunningState>().Input!.Value.GetProperty("questions")[0];
        asking.GetProperty("question").GetString().ShouldBe("Pick one");
        asking.GetProperty("options").GetArrayLength().ShouldBe(2);
        var answered = parts[^1].State.ShouldBeOfType<ToolCompletedState>();
        answered.Metadata!.Value.GetProperty("answers")[0][0].GetString().ShouldBe("B");
    }

    [Fact]
    public async Task A_question_waits_on_the_user_and_the_answer_starts_the_turn_again()
    {
        var statuses = (await MapAsync())
            .Where(e => e.Type == EventTypes.SessionStatus)
            .Select(e => e.Payload!.Value.GetProperty("status").GetProperty("type").GetString())
            .ToList();

        // Four turns start busy; the answered question waits then goes on; the dismissed one waits then ends.
        statuses.ShouldBe(
        [
            ActivityStatuses.Busy,
            ActivityStatuses.Busy,
            ActivityStatuses.Busy, ActivityStatuses.WaitingInput, ActivityStatuses.Busy,
            ActivityStatuses.Busy, ActivityStatuses.WaitingInput, ActivityStatuses.Busy,
        ]);
    }

    [Fact]
    public async Task A_dismissed_question_ends_the_turn_without_a_failure()
    {
        var events = await MapAsync();
        var lastTurn = events.Skip(events.FindLastIndex(e => IsStatus(e, ActivityStatuses.WaitingInput))).ToList();

        var tool = lastTurn.Where(e => e.Type == EventTypes.MessagePartUpdated).Select(Translate).OfType<MessagePartUpdated>()
            .Select(p => p.Payload.Part).OfType<ToolMessageEventPart>().ShouldHaveSingleItem();
        tool.State.ShouldBeOfType<ToolErrorState>().Error.ShouldBe("The user dismissed this question");
        lastTurn[^1].Type.ShouldBe(EventTypes.SessionIdle);
        lastTurn.ShouldNotContain(e => e.Type == EventTypes.SessionError);
    }

    [Fact]
    public async Task Every_tool_and_question_event_translates()
    {
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);
        var events = await MapAsync();

        // Statuses are read raw by the relay (activity, waiting on the user); the translator only turns some into turns.
        events.Where(e => translator.Translate(e with { FleetSessionId = FleetSession }) is null && e.Type != EventTypes.SessionStatus)
            .Select(e => e.Type + " " + e.Payload!.Value.GetRawText())
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task A_form_created_event_is_routed_to_the_session_it_asks_in()
    {
        var events = await OpenCode2Fixtures.ReadEventsAsync("tools-questions.sse");

        events.Where(e => e.Type.StartsWith("form.", StringComparison.Ordinal))
            .ShouldAllBe(e => e.SessionId == OpenCode2Fixtures.ToolsSession);
    }

    [Fact]
    public void A_form_that_is_not_a_question_does_not_stop_the_turn()
    {
        var mapper = new OpenCode2Mapper(FleetSession);

        mapper.Map(Event("form.created", """
            {"form":{"id":"frm_1","sessionID":"ses_1","title":"Sign in","metadata":{"kind":"auth"},"fields":[{"key":"token","type":"string"}]}}
            """)).ShouldBeEmpty();
        mapper.Map(Event("form.replied", """{"id":"frm_1","sessionID":"ses_1","answer":{"token":"x"}}""")).ShouldBeEmpty();
    }

    [Fact]
    public void A_tool_that_returns_a_file_shows_it_as_a_file_part()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        mapper.Map(Event("session.tool.input.started", """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","name":"fleet_browser_screenshot"}"""));
        mapper.Map(Event("session.tool.called", """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","input":{}}"""));

        var events = mapper.Map(Event("session.tool.success", """
            {"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","content":[{"type":"text","text":"Took a screenshot."},{"type":"file","uri":"data:image/png;base64,AAAA","mime":"image/png","name":"shot.png"}]}
            """));

        var parts = events.Select(Translate).OfType<MessagePartUpdated>().Select(p => p.Payload.Part).ToList();
        parts[0].ShouldBeOfType<ToolMessageEventPart>().State.ShouldBeOfType<ToolCompletedState>().Output!.Value.GetString().ShouldBe("Took a screenshot.");
        var file = parts[1].ShouldBeOfType<FileMessageEventPart>();
        file.Id.ShouldBe(OpenCode2Mapper.ToolFilePartId("msg_1", "call_1", 0));
        file.Mime.ShouldBe("image/png");
        file.Url.ShouldBe("data:image/png;base64,AAAA");
        file.Filename.ShouldBe("shot.png");
    }

    [Theory]
    // Recorded from 2.0.8: V2's edit and write both name their file in input.path, and send no file event of their own.
    [InlineData("edit", """{"path":"/work/proj/note.txt","oldString":"hello","newString":"edited"}""", "/work/proj/note.txt")]
    [InlineData("write", """{"path":"/work/proj/note.txt","content":"written\n"}""", "/work/proj/note.txt")]
    [InlineData("write", """{"path":"src/new.ts","content":"x"}""", "/work/proj/src/new.ts")]
    public void A_file_the_agent_wrote_is_reported_as_written_and_changed(string tool, string input, string expected)
    {
        var mapper = new OpenCode2Mapper(FleetSession, "/elsewhere");
        mapper.Map(Event("session.tool.input.started", $$"""{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","name":"{{tool}}"}"""));
        mapper.Map(Event("session.tool.called", $$"""{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","input":{{input}}}"""));

        var events = mapper.Map(Event("session.tool.success", """
            {"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","content":[{"type":"text","text":"Done"}],"metadata":{"truncated":false}}
            """, directory: "/work/proj"));

        var written = events.Select(Translate).OfType<FilesWritten>().ShouldHaveSingleItem();
        written.Payload.SessionId.ShouldBe(FleetSession);
        written.Payload.MessageId.ShouldBe("msg_1");
        written.Payload.Paths.ShouldBe([Path.GetFullPath(expected)]);

        // What open files and the file list reload on.
        var changed = events.Select(Translate).OfType<FilesChanged>().ShouldHaveSingleItem();
        changed.Payload.SessionId.ShouldBe(FleetSession);
        var file = changed.Payload.Files.ShouldHaveSingleItem();
        file.Path.ShouldBe(Path.GetFullPath(expected));
        file.ChangeType.ShouldBe("change");
    }

    [Fact]
    public void A_relative_path_is_taken_from_the_session_folder_when_the_event_names_none()
    {
        var mapper = new OpenCode2Mapper(FleetSession, "/work/proj");
        mapper.Map(Event("session.tool.input.started", """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","name":"edit"}"""));
        mapper.Map(Event("session.tool.called", """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","input":{"path":"note.txt","oldString":"a","newString":"b"}}"""));

        var events = mapper.Map(Event("session.tool.success", """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","content":[]}"""));

        events.Select(Translate).OfType<FilesWritten>().ShouldHaveSingleItem().Payload.Paths.ShouldBe([Path.GetFullPath("/work/proj/note.txt")]);
    }

    [Theory]
    [InlineData("edit", "session.tool.failed")]
    [InlineData("read", "session.tool.success")]
    public void Only_a_file_tool_that_finished_reports_a_file(string tool, string ended)
    {
        var mapper = new OpenCode2Mapper(FleetSession, "/work/proj");
        mapper.Map(Event("session.tool.input.started", $$"""{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","name":"{{tool}}"}"""));
        mapper.Map(Event("session.tool.called", """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","input":{"path":"note.txt","oldString":"a","newString":"b"}}"""));

        var events = mapper.Map(Event(ended, """{"sessionID":"ses_1","assistantMessageID":"msg_1","id":"call_1","content":[],"error":{"type":"x","message":"no"}}"""));

        events.ShouldNotContain(e => e.Type == EventTypes.FilesWritten || e.Type == EventTypes.FileWatcherUpdated);
    }

    [Fact]
    public void A_permission_ask_maps_to_nothing_the_session_answers_it()
    {
        new OpenCode2Mapper(FleetSession).Map(Event("permission.asked", """
            {"id":"per_1","sessionID":"ses_1","action":"shell","resources":["echo hi"],"save":["echo *"]}
            """)).ShouldBeEmpty();
    }

    private static async Task<List<HarnessEvent>> MapAsync()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var mapped = new List<HarnessEvent>();
        foreach (var evt in await OpenCode2Fixtures.ReadEventsAsync("tools-questions.sse"))
        {
            if (evt.SessionId == OpenCode2Fixtures.ToolsSession)
                mapped.AddRange(mapper.Map(evt));
        }

        return mapped;
    }

    private static async Task<List<ToolMessageEventPart>> ToolPartsAsync(string callId)
        => (await MapAsync())
            .Where(e => e.Type == EventTypes.MessagePartUpdated)
            .Select(Translate)
            .OfType<MessagePartUpdated>()
            .Select(p => p.Payload.Part)
            .OfType<ToolMessageEventPart>()
            .Where(p => p.CallId == callId)
            .ToList();

    private static DomainEvent? Translate(HarnessEvent evt)
        => new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance).Translate(evt with { FleetSessionId = FleetSession });

    private static bool IsStatus(HarnessEvent evt, string status)
        => evt.Type == EventTypes.SessionStatus && evt.Payload!.Value.GetProperty("status").GetProperty("type").GetString() == status;

    private static OpenCode2Event Event(string type, string data, string? directory = null) => new()
    {
        Id = "evt_1",
        Created = 1_789_763_753_000,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
        Location = directory is null ? null : new OpenCode2EventLocation { Directory = directory },
    };
}
