using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// One <c>claude</c> process runs a session's prompts one after another, so work an agent leaves running in the
/// background outlives its turn. A stand-in claude (a shell script that speaks stream-json the way Claude Code 2.1.289
/// does) records each start's arguments and every stdin line.
/// </summary>
public sealed class ClaudeCodeLongLivedProcessTests : IAsyncDisposable
{
    private const int IdleSeconds = 60;

    private readonly string _directory = Directory.CreateTempSubdirectory("fleet-cc-long-").FullName;
    private readonly InMemoryMessageRepository _messages = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLogger _log = new();
    private readonly ConcurrentQueue<HarnessEvent> _events = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemorySessionRepository _sessions = new();
    private ClaudeCodeHarnessSession? _session;
    private Task _reader = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        await _reader.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Several_prompts_run_on_one_process()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();

        await PromptAsync("one");
        var pid = session.ProcessId;
        await PromptAsync("two");
        await PromptAsync("three");

        Starts().ShouldBe(1);
        session.ProcessId.ShouldBe(pid);
        Prompts().ShouldBe(["one", "two", "three"]);
        AssistantText().ShouldBe(["Reply 1", "Reply 2", "Reply 3"]);
        Count(EventTypes.SessionIdle).ShouldBe(3);
    }

    [Fact]
    public async Task Background_work_outlives_its_turn_and_Claude_goes_on_by_itself_when_it_finishes()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();

        await PromptAsync("start background");
        session.BackgroundWork.ShouldBe(["task-1"]);
        session.ProcessId.ShouldNotBeNull();

        // The task finishes after the turn: Claude Code says so and starts a turn with no prompt from Fleet.
        await WaitForAsync(() => Count(EventTypes.SessionIdle) == 2);
        session.BackgroundWork.ShouldBeEmpty();
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        Statuses().ShouldBe(["busy", "idle", "busy", "idle"]);
        AssistantText().ShouldBe(["Started it.", "The background command finished: done"]);
        _messages.All.Count(m => m.Role == "user").ShouldBe(1);
        Starts().ShouldBe(1);

        // And the next prompt goes to the same process.
        await PromptAsync("after");
        Starts().ShouldBe(1);
        AssistantText().Last().ShouldBe("Reply 2");
    }

    [Fact]
    public async Task A_prompt_sent_while_Claude_runs_a_turn_of_its_own_waits_for_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start slow background");
        await WaitForAsync(() => Statuses().Count >= 3); // busy, idle, and busy again: Claude's own turn

        // Sent while Claude's own turn runs: it goes in once that turn is over.
        await _session!.SendPromptAsync("next", null, CancellationToken.None);
        await WaitForAsync(() => Statuses().Count >= 6);

        Statuses().ShouldBe(["busy", "idle", "busy", "idle", "busy", "idle"]);
        AssistantText().ShouldBe(["Started it.", "The background command finished: done", "Reply 2"]);
    }

    [Fact]
    public async Task Interrupt_stops_the_turn_and_keeps_the_process_and_its_background_work()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");
        var pid = session.ProcessId;

        await session.SendPromptAsync("hang", null, CancellationToken.None);
        await WaitForAsync(() => Tools().Any(t => t.ToolCallId == "toolu_hang"));

        await session.AbortAsync(CancellationToken.None);

        StdinLines().ShouldContain(line => line.Contains("\"subtype\":\"interrupt\""));
        session.ProcessId.ShouldBe(pid);
        session.BackgroundWork.ShouldBe(["task-1"]);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        await WaitForAsync(() => Count(EventTypes.SessionIdle) == 2);
        var hung = Tools().Single(t => t.ToolCallId == "toolu_hang");
        hung.State.ShouldBe(ToolUseState.Error);
        AssistantText().ShouldNotContain(text => text.StartsWith("Claude Code", StringComparison.Ordinal));

        await PromptAsync("again");
        Starts().ShouldBe(1);
    }

    [Fact]
    public async Task An_interrupt_Claude_doesnt_answer_falls_back_to_ending_the_process()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start(honoursInterrupt: false);
        await PromptAsync("one");
        await session.SendPromptAsync("hang", null, CancellationToken.None);
        await WaitForAsync(() => Tools().Any(t => t.ToolCallId == "toolu_hang"));

        await session.AbortAsync(CancellationToken.None);

        session.ProcessId.ShouldBeNull();
        await WaitForAsync(() => Count(EventTypes.SessionIdle) == 2);
        AssistantText().ShouldNotContain(text => text.StartsWith("Claude Code", StringComparison.Ordinal));

        // The next prompt picks the conversation up in a new process.
        await PromptAsync("again");
        Starts().ShouldBe(2);
        ShouldHaveArgument(Arguments(1), "--resume", "cc-1");
    }

    [Fact]
    public async Task An_idle_process_with_no_background_work_stops_and_the_next_prompt_resumes()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("one");

        _time.Advance(TimeSpan.FromSeconds(IdleSeconds - 1));
        session.ProcessId.ShouldNotBeNull();
        _time.Advance(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => session.ProcessId is null);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        _log.Messages.ShouldNotContain(m => m.Contains("work is lost", StringComparison.Ordinal));

        await PromptAsync("two");

        Starts().ShouldBe(2);
        Arguments(0).ShouldNotContain("--resume");
        ShouldHaveArgument(Arguments(1), "--resume", "cc-1");
        AssistantText().ShouldBe(["Reply 1", "Reply 1"]);
    }

    [Fact]
    public async Task An_idle_process_with_background_work_keeps_running()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");

        _time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(200);

        session.ProcessId.ShouldNotBeNull();
        session.BackgroundWork.ShouldBe(["task-1"]);
    }

    [Fact]
    public async Task Stopping_the_session_ends_the_process_and_logs_the_work_it_loses()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");

        await session.StopAsync(CancellationToken.None);

        session.ProcessId.ShouldBeNull();
        _log.Messages.ShouldContain(m => m.Contains("the session stopped", StringComparison.Ordinal)
            && m.Contains("1 background task(s)", StringComparison.Ordinal)
            && m.Contains("task-1 (sleep 600)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archiving_the_session_ends_the_process_and_the_next_prompt_resumes()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");

        await session.ArchiveAsync(CancellationToken.None);

        session.ProcessId.ShouldBeNull();
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        _log.Messages.ShouldContain(m => m.Contains("the session was archived", StringComparison.Ordinal)
            && m.Contains("task-1 (sleep 600)", StringComparison.Ordinal));

        await PromptAsync("two");

        Starts().ShouldBe(2);
        ShouldHaveArgument(Arguments(1), "--resume", "cc-1");
    }

    [Fact]
    public async Task A_new_model_is_switched_to_on_the_running_process()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("one", new PromptOptions { ModelId = "sonnet" });
        await PromptAsync("two", new PromptOptions { ModelId = "opus" });
        await PromptAsync("three");

        Starts().ShouldBe(1);
        ShouldHaveArgument(Arguments(0), "--model", "sonnet");
        var switches = StdinLines().Where(line => line.Contains("\"set_model\"", StringComparison.Ordinal)).ToList();
        JsonDocument.Parse(switches.ShouldHaveSingleItem()).RootElement.GetProperty("request").GetProperty("model").GetString()
            .ShouldBe("opus");
    }

    [Fact]
    public async Task A_model_Claude_wont_switch_to_starts_a_new_process_on_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start(switchesModel: false);
        await PromptAsync("one", new PromptOptions { ModelId = "sonnet" });
        await PromptAsync("two", new PromptOptions { ModelId = "opus" });

        Starts().ShouldBe(2);
        ShouldHaveArgument(Arguments(1), "--model", "opus");
        ShouldHaveArgument(Arguments(1), "--resume", "cc-1");
    }

    [Fact]
    public async Task Permission_changes_switch_the_mode_or_restart_when_Claude_cant()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        await PromptAsync("one");

        // Ask → Edits: the same process, told the new mode.
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Edits), CancellationToken.None);
        await PromptAsync("two");
        Starts().ShouldBe(1);
        StdinLines().ShouldContain(line => line.Contains("\"set_permission_mode\"") && line.Contains("\"acceptEdits\""));

        // Edits → All: Claude Code won't switch into bypassPermissions, so a process started in it. It still asks Fleet,
        // which answers at once, so the agent keeps AskUserQuestion.
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.All), CancellationToken.None);
        await PromptAsync("three");
        Starts().ShouldBe(2);
        Arguments(0).ShouldContain("--permission-prompt-tool");
        Arguments(1).ShouldContain("--permission-prompt-tool");
        ShouldHaveArgument(Arguments(1), "--permission-mode", "bypassPermissions");
        ShouldHaveArgument(Arguments(1), "--resume", "cc-1");
    }

    [Fact]
    public async Task After_Fleet_restarts_the_first_prompt_resumes_the_conversation()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start(claudeSessionId: "cc-before-restart");
        await PromptAsync("one");

        ShouldHaveArgument(Arguments(0), "--resume", "cc-before-restart");
    }

    [Fact]
    public async Task A_process_that_dies_between_turns_is_replaced_by_the_next_prompt()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");
        System.Diagnostics.Process.GetProcessById(session.ProcessId!.Value).Kill();
        await WaitForAsync(() => session.ProcessId is null);

        _log.Messages.ShouldContain(m => m.Contains("it exited", StringComparison.Ordinal) && m.Contains("work is lost", StringComparison.Ordinal));
        Count(EventTypes.SessionIdle).ShouldBe(1);

        await PromptAsync("two");
        Starts().ShouldBe(2);
        AssistantText().Last().ShouldBe("Reply 1");
    }

    [Fact]
    public async Task Stop_ends_one_background_task_and_leaves_the_process_running()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");
        var pid = session.ProcessId;
        Work(EventTypes.WorkStarted).Select(r => (r.WorkId, r.Kind, r.CanStop, r.CanReadOutput)).ShouldBe([("task-1", "shell", true, true)]);

        (await session.StopWorkAsync("task-1", CancellationToken.None)).ShouldBeTrue();

        StdinLines().ShouldContain(line => line.Contains("\"subtype\":\"stop_task\"") && line.Contains("\"task_id\":\"task-1\""));
        await WaitForAsync(() => Work(EventTypes.WorkEnded).Count == 1);
        Work(EventTypes.WorkEnded).Single().ShouldSatisfyAllConditions(
            r => r.EndedReason.ShouldBe(WorkEndedReasons.Cancelled),
            r => r.Detail.ShouldBe("stopped"));
        session.ProcessId.ShouldBe(pid);
        session.BackgroundWork.ShouldBeEmpty();

        // Gone now: there's nothing to stop.
        (await session.StopWorkAsync("task-1", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Work_a_dead_process_was_running_is_lost_and_nothing_runs_after_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await PromptAsync("start lasting background");
        (await session.GetRunningWorkAsync(CancellationToken.None))!.Select(r => r.WorkId).ShouldBe(["task-1"]);

        System.Diagnostics.Process.GetProcessById(session.ProcessId!.Value).Kill();
        await WaitForAsync(() => Work(EventTypes.WorkEnded).Count == 1);

        Work(EventTypes.WorkEnded).Single().ShouldSatisfyAllConditions(
            r => r.WorkId.ShouldBe("task-1"),
            r => r.EndedReason.ShouldBe(WorkEndedReasons.Lost));
        (await session.GetRunningWorkAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Fleets_notes_to_the_model_go_ahead_of_the_prompt()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start();
        await PromptAsync("one", new PromptOptions { ModelNotes = ["Note from Fleet: lost work."] });

        var content = JsonDocument.Parse(StdinLines().Single()).RootElement.GetProperty("message").GetProperty("content");
        content.EnumerateArray().Select(block => block.GetProperty("text").GetString()).ShouldBe(["Note from Fleet: lost work.", "one"]);

        // The conversation keeps what the user wrote.
        _messages.All.Single(m => m.Role == "user").PartsJson.ShouldNotContain("lost work");
    }

    [Fact]
    public async Task A_steered_prompt_goes_into_the_running_turn_and_is_saved_where_it_was_sent()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await session.SendPromptAsync("steer me", null, CancellationToken.None);
        await WaitForAsync(() => Tools().Any(t => t.ToolCallId == "toolu_steer"));

        // It doesn't wait for the turn: it's written at once, and Claude Code reads it at the step's end.
        await session.SendPromptAsync("change course", new PromptOptions { Delivery = PromptDelivery.Steer, MessageId = "msg_fleet_steer" }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => Count(EventTypes.SessionIdle) == 1);

        Prompts().ShouldBe(["steer me", "change course"]);
        Statuses().ShouldBe(["busy", "idle"]);
        AssistantText().ShouldBe(["Changed course."]);

        // Claude Code doesn't write it back: Fleet keeps it, under the id it was shown with, as sent mid-turn.
        var steered = MessagePersistenceService.ToHarnessMessage(_messages.All.Single(m => m.Id == "msg_fleet_steer"));
        steered.Role.ShouldBe("user");
        steered.Steered.ShouldBeTrue();
        steered.TextContent.ShouldBe("change course");
    }

    [Fact]
    public async Task A_steered_prompt_with_no_turn_running_starts_one()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start();
        await PromptAsync("one", new PromptOptions { Delivery = PromptDelivery.Steer });

        Statuses().ShouldBe(["busy", "idle"]);
        AssistantText().ShouldBe(["Reply 1"]);
    }

    [Fact]
    public async Task A_question_waits_on_the_user_and_their_answer_goes_back_with_the_questions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await session.SendPromptAsync("ask me", null, CancellationToken.None);
        await WaitForAsync(() => Statuses().Contains(ActivityStatuses.WaitingInput));
        _events.ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);

        await session.AnswerQuestionAsync("toolu_ask", [["Blue"]], CancellationToken.None);
        await WaitForAsync(() => Count(EventTypes.SessionIdle) == 1 && StdinLines().Any(line => line.Contains("\"control_response\"")));

        // allow, with the questions it asked and the answers by question text.
        var answer = JsonDocument.Parse(StdinLines().Single(line => line.Contains("\"control_response\""))).RootElement
            .GetProperty("response");
        answer.GetProperty("request_id").GetString().ShouldBe("req_ask");
        var allowed = answer.GetProperty("response");
        allowed.GetProperty("behavior").GetString().ShouldBe("allow");
        allowed.GetProperty("updatedInput").GetProperty("questions")[0].GetProperty("question").GetString().ShouldBe("Which colour?");
        allowed.GetProperty("updatedInput").GetProperty("answers").GetProperty("Which colour?").GetString().ShouldBe("Blue");

        Statuses().ShouldBe(["busy", ActivityStatuses.WaitingInput, ActivityStatuses.Busy, "idle"]);
        var question = Tools().Single(t => t.ToolCallId == "toolu_ask");
        question.ToolName.ShouldBe("question");
        question.State.ShouldBe(ToolUseState.Completed);
        question.Metadata!.Value.GetProperty("answers")[0][0].GetString().ShouldBe("Blue");
    }

    [Fact]
    public async Task A_dismissed_question_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start();
        await session.SendPromptAsync("ask me", null, CancellationToken.None);
        await WaitForAsync(() => Statuses().Contains(ActivityStatuses.WaitingInput));

        await session.RejectQuestionAsync("req_ask", CancellationToken.None);
        // The stand-in claude records what it reads a moment after Fleet writes it.
        await WaitForAsync(() => StdinLines().Any(line => line.Contains("\"control_response\"")));

        var refusal = JsonDocument.Parse(StdinLines().Single(line => line.Contains("\"control_response\""))).RootElement
            .GetProperty("response").GetProperty("response");
        refusal.GetProperty("behavior").GetString().ShouldBe("deny");
        await Should.ThrowAsync<KeyNotFoundException>(() => session.AnswerQuestionAsync("toolu_ask", [["Red"]], CancellationToken.None));
    }

    [Fact]
    public async Task Effort_starts_the_process_with_it_and_a_change_switches_it_in_place()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start();
        await PromptAsync("one", new PromptOptions { ModelId = "sonnet", Effort = "high" });
        ShouldHaveArgument(Arguments(0), "--effort", "high");

        await PromptAsync("two", new PromptOptions { Effort = "low" });
        Starts().ShouldBe(1);
        var applied = JsonDocument.Parse(StdinLines().Single(line => line.Contains("apply_flag_settings"))).RootElement;
        applied.GetProperty("request").GetProperty("settings").GetProperty("effortLevel").GetString().ShouldBe("low");

        // No effort named: the process keeps what it runs at.
        await PromptAsync("three");
        StdinLines().Count(line => line.Contains("apply_flag_settings")).ShouldBe(1);
    }

    [Fact]
    public async Task Each_process_calls_Fleet_with_a_token_of_its_own_that_ends_with_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tokens = new ClaudeCodeBridgeTokenRegistry();
        var session = Start(bridgeTokens: tokens);
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        await PromptAsync("one");

        var first = Environment(0);
        var token = first["FLEET_BRIDGE_TOKEN"];
        first["FLEET_URL"].ShouldBe($"http://127.0.0.1:5999/agent/{token}");
        first["FLEET_HARNESS_SESSION_ID"].ShouldBe("fleet-cc");
        tokens.Find(token).ShouldBe(new WeaveFleet.Application.Canvases.HarnessCanvasCaller("fleet-cc", TestUserContext.DefaultUserId));
        (await new ClaudeCodeCanvasCallerResolver(tokens).ResolveAsync(token, "")).ShouldNotBeNull();

        // A process started to switch into bypassPermissions gets a new token; the old one stops working.
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.All), CancellationToken.None);
        await PromptAsync("two");
        Starts().ShouldBe(2);
        var second = Environment(1)["FLEET_BRIDGE_TOKEN"];
        second.ShouldNotBe(token);
        tokens.Find(token).ShouldBeNull();
        new ClaudeCodeBridgeTokens(tokens).IsKnown(second).ShouldBeTrue();

        await session.StopAsync(CancellationToken.None);
        tokens.Find(second).ShouldBeNull();
    }

    [Fact]
    public async Task A_process_gets_Fleets_tools_from_Fleets_MCP_server_and_the_token_stays_off_its_command_line()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start(bridgeTokens: new ClaudeCodeBridgeTokenRegistry());
        await PromptAsync("one");

        var arguments = Arguments(0);
        ShouldHaveArgument(arguments, "--mcp-config", """{"mcpServers":{"fleet":{"type":"http","url":"${FLEET_URL}/mcp"}}}""");
        arguments.ShouldNotContain("--strict-mcp-config");
        arguments.ShouldNotContain(argument => argument.Contains(Environment(0)["FLEET_BRIDGE_TOKEN"], StringComparison.Ordinal));

        // Every tool the session gets is allowed up front, except fleet_app_start, which asks as a shell command does.
        // Memory, messages between sessions and workflows are off, and the session isn't a workflow step.
        AllowedTools(0).ShouldBe(
        [
            "mcp__fleet__fleet_canvas_list", "mcp__fleet__fleet_canvas_open", "mcp__fleet__fleet_canvas_read",
            "mcp__fleet__fleet_canvas_patch", "mcp__fleet__fleet_canvas_focus", "mcp__fleet__fleet_page_show",
            "mcp__fleet__fleet_browser_open", "mcp__fleet__fleet_browser_read", "mcp__fleet__fleet_browser_act",
            "mcp__fleet__fleet_browser_screenshot", "mcp__fleet__fleet_session_read",
        ]);
    }

    [Fact]
    public async Task Without_a_Fleet_address_a_process_gets_no_Fleet_tools()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start();
        await PromptAsync("one");

        Arguments(0).ShouldNotContain("--mcp-config");
        Arguments(0).ShouldNotContain("--allowedTools");
    }

    [Fact]
    public async Task A_switch_that_changes_takes_a_new_process_that_resumes_with_the_tools_it_gives_now()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start(bridgeTokens: new ClaudeCodeBridgeTokenRegistry());
        await PromptAsync("one");
        AllowedTools(0).ShouldNotContain("mcp__fleet__fleet_memory_save");

        await _preferences.SetAsync(AgentMemory.PreferenceKey, "true");
        await _preferences.SetAsync(SessionMessages.PreferenceKey, "true");
        await _preferences.SetAsync(WeaveFleet.Application.Browser.AgentBrowserSettings.EnabledKey, "false");
        await PromptAsync("two");

        Starts().ShouldBe(2);
        ShouldHaveArgument(Arguments(1), "--resume", "cc-1");
        AllowedTools(1).ShouldContain("mcp__fleet__fleet_memory_save");
        AllowedTools(1).ShouldContain("mcp__fleet__fleet_memory_forget");
        AllowedTools(1).ShouldContain("mcp__fleet__fleet_message");
        AllowedTools(1).ShouldNotContain("mcp__fleet__fleet_browser_read");
        AllowedTools(1).ShouldNotContain("mcp__fleet__fleet_browser_act");

        // Nothing changed since: the process carries on.
        await PromptAsync("three");
        Starts().ShouldBe(2);
    }

    [Fact]
    public async Task A_switch_that_changes_while_work_runs_in_the_background_waits_for_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Start(bridgeTokens: new ClaudeCodeBridgeTokenRegistry());
        await PromptAsync("start lasting background");

        await _preferences.SetAsync(AgentMemory.PreferenceKey, "true");
        await PromptAsync("two");

        Starts().ShouldBe(1);
        session.BackgroundWork.ShouldBe(["task-1"]);
    }

    [Fact]
    public async Task Only_a_workflow_step_the_agent_finishes_gets_the_step_tool()
    {
        if (OperatingSystem.IsWindows())
            return;

        await _preferences.SetAsync(FleetWorkflows.PreferenceKey, "true");
        _sessions.Seed(new Session { Id = "fleet-cc", WorkspaceId = "ws-1", Directory = _directory, Title = "Plan", WorkflowRunId = "run-1" });
        Start(bridgeTokens: new ClaudeCodeBridgeTokenRegistry());
        await PromptAsync("one");

        AllowedTools(0).ShouldContain("mcp__fleet__fleet_step_done");
    }

    [Fact]
    public async Task Images_go_ahead_of_the_prompt_as_image_blocks()
    {
        if (OperatingSystem.IsWindows())
            return;

        Start();
        await PromptAsync("what is this?", new PromptOptions
        {
            Attachments = [new HarnessAttachment("image/png", "red.png", "iVBORw0KGgo="), new HarnessAttachment("application/pdf", "doc.pdf", "JVBERi0=")],
        });

        var content = JsonDocument.Parse(StdinLines().Single()).RootElement.GetProperty("message").GetProperty("content");
        content.GetArrayLength().ShouldBe(2);
        content[0].GetProperty("type").GetString().ShouldBe("image");
        content[0].GetProperty("source").GetProperty("media_type").GetString().ShouldBe("image/png");
        content[0].GetProperty("source").GetProperty("data").GetString().ShouldBe("iVBORw0KGgo=");
        content[1].GetProperty("text").GetString().ShouldBe("what is this?");
    }

    // -----------------------------------------------------------------------

    private Dictionary<string, string> Environment(int start)
        => File.ReadAllLines(Path.Combine(_directory, $"env-{start}.txt"))
            .Select(line => line.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    private List<WorkReport> Work(string type)
        => _events.Where(e => e.Type == type)
            .Select(e => JsonSerializer.Deserialize(e.Payload!.Value, WeaveFleet.Infrastructure.InfrastructureJsonContext.Default.WorkReport)!)
            .ToList();

    private ClaudeCodeHarnessSession Start(
        bool honoursInterrupt = true, bool switchesModel = true, string? claudeSessionId = null, ClaudeCodeBridgeTokenRegistry? bridgeTokens = null)
    {
        var delegations = new InMemoryDelegationRepository();
        var sessions = _sessions;
        var connections = new FakeDbConnectionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(_messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, _messages, delegations, sessions, new InMemorySmartLinkRepository(), new InMemoryOutboxRepository(), new FakeOutboxDispatcher()));

        // Which of Fleet's tools a process gets, from the owner's settings.
        services.AddSingleton(new FleetOptions());
        services.AddSingleton<IUserPreferenceRepository>(_preferences);
        services.AddSingleton<IBackgroundUserScope, NoUserScope>();
        services.AddSingleton<SessionMessagesFeature>();
        services.AddSingleton<WeaveFleet.Application.Machines.AgentHandoffFeature>();
        services.AddSingleton<AgentMemoryFeature>();
        services.AddSingleton<WorkflowsFeature>();
        services.AddSingleton<WeaveFleet.Application.Mods.ModsSafeMode>();
        services.AddSingleton<IUserContext>(new TestUserContext(TestUserContext.DefaultUserId));
        services.AddSingleton<WeaveFleet.Application.Mods.ModsFeature>();
        services.AddSingleton<FleetToolSettings>();

        _session = new ClaudeCodeHarnessSession(
            instanceId: "cc-instance",
            fleetSessionId: "fleet-cc",
            workingDirectory: _directory,
            config: new ClaudeCodeOptions { BinaryPath = FakeClaude(honoursInterrupt, switchesModel), IdleShutdownSeconds = IdleSeconds },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(2),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: _log,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            claudeSessionId: claudeSessionId,
            bridgeTokens: bridgeTokens,
            fleetUrl: () => "http://127.0.0.1:5999/")
        {
            Time = _time,
        };

        var session = _session;
        _reader = Task.Run(async () =>
        {
            await foreach (var evt in session.SubscribeAsync(CancellationToken.None))
                _events.Enqueue(evt);
        });
        return session;
    }

    /// <summary>Sends a prompt and waits for its turn to end.</summary>
    private async Task PromptAsync(string text, PromptOptions? options = null)
    {
        var idle = Count(EventTypes.SessionIdle);
        await _session!.SendPromptAsync(text, options, CancellationToken.None);
        await WaitForAsync(() => Count(EventTypes.SessionIdle) > idle);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!Holds())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never held.");
            await Task.Delay(20);
        }

        bool Holds()
        {
            try
            {
                return condition();
            }
            catch (ArgumentException)
            {
                // The in-memory message repository isn't safe to read while the session writes to it; look again.
                return false;
            }
        }
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>The tools a start may use without asking (<c>--allowedTools</c>), by Claude Code's names.</summary>
    private string[] AllowedTools(int start)
    {
        var arguments = Arguments(start);
        var at = Array.IndexOf(arguments, "--allowedTools");
        return at < 0 ? [] : arguments[at + 1].Split(',');
    }

    private static void ShouldHaveArgument(string[] arguments, string name, string value)
    {
        var at = Array.IndexOf(arguments, name);
        at.ShouldBeGreaterThanOrEqualTo(0, $"No {name} in: {string.Join(' ', arguments)}");
        arguments[at + 1].ShouldBe(value);
    }

    private int Count(string type) => _events.Count(e => e.Type == type);

    /// <summary>The session's busy/idle story, in order.</summary>
    private List<string> Statuses()
        => _events
            .Select(e => e.Type == EventTypes.SessionIdle
                ? "idle"
                : e.Type == EventTypes.SessionStatus ? e.Payload!.Value.GetProperty("status").GetProperty("type").GetString() : null)
            .OfType<string>()
            .ToList();

    private int Starts() => File.Exists(Path.Combine(_directory, "starts.txt"))
        ? File.ReadAllLines(Path.Combine(_directory, "starts.txt")).Length
        : 0;

    private string[] Arguments(int start) => File.ReadAllLines(Path.Combine(_directory, $"args-{start}.txt"));

    /// <summary>What Fleet wrote to claude, less its ask for the account's usage limits (get_usage), which goes to every process.</summary>
    private string[] StdinLines()
        => [.. File.ReadAllLines(Path.Combine(_directory, "stdin.txt")).Where(line => !line.Contains("\"get_usage\"", StringComparison.Ordinal))];

    private List<string> Prompts()
        => StdinLines()
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(json => json.GetProperty("type").GetString() == "user")
            .Select(json => json.GetProperty("message").GetProperty("content") is { ValueKind: JsonValueKind.String } text
                ? text.GetString()!
                : json.GetProperty("message").GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString()!)
            .ToList();

    private List<HarnessMessage> Conversation()
        => MessagePersistenceService.ToHarnessMessages(
                _messages.All.Where(m => m.Role == "assistant").OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            .ToList();

    private List<string> AssistantText() => Conversation().SelectMany(m => m.Parts).OfType<TextPart>().Select(t => t.Text).ToList();

    private List<ToolUsePart> Tools() => Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ToList();

    /// <summary>
    /// A stand-in for <c>claude</c> that runs prompts until its stdin closes. Each start appends to <c>starts.txt</c> and
    /// writes its arguments to <c>args-N.txt</c>; every stdin line goes to <c>stdin.txt</c>. Prompts: "start background"
    /// starts a task that finishes a second after the turn, and Claude then goes on by itself; "start slow background"
    /// does the same, with a turn of its own that takes a while; "start lasting background" starts one that doesn't finish; "hang" starts a tool call that
    /// only an interrupt ends; anything else gets "Reply N".
    /// </summary>
    private string FakeClaude(bool honoursInterrupt, bool switchesModel)
    {
        var claude = Path.Combine(_directory, "claude");
        var interrupt = honoursInterrupt
            ? """
                  echo '{"type":"control_response","response":{"subtype":"success","request_id":"'"$rid"'","response":{"still_queued":[]}}}'
                  echo '{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"parent_tool_use_id":null}'
                  echo '{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["aborted_streaming"],"session_id":"cc-1"}'
              """
            : "      :";
        var setModel = switchesModel
            ? """      echo '{"type":"control_response","response":{"subtype":"success","request_id":"'"$rid"'"}}'"""
            : """      echo '{"type":"control_response","response":{"subtype":"error","request_id":"'"$rid"'","error":"model not changed"}}'""";
        var script = $$$"""
            #!/bin/sh
            dir='{{{_directory}}}'
            start=0
            [ -f "$dir/starts.txt" ] && start=$(wc -l < "$dir/starts.txt")
            printf '%s\n' "$@" > "$dir/args-$start.tmp" && mv "$dir/args-$start.tmp" "$dir/args-$start.txt"
            env | grep '^FLEET_' | sort > "$dir/env-$start.txt"
            echo $$ >> "$dir/starts.txt"
            n=0
            steering=0
            wake() {
              sleep "$1"
              echo '{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"cc-1"}'
              echo '{"type":"system","subtype":"task_notification","task_id":"task-'$2'","status":"completed","summary":"done","session_id":"cc-1"}'
              echo '{"type":"system","subtype":"init","session_id":"cc-1","model":"claude-test"}'
              sleep "$3"
              echo '{"type":"assistant","message":{"id":"msg_wake_'$2'","role":"assistant","content":[{"type":"text","text":"The background command finished: done"}]},"parent_tool_use_id":null}'
              echo '{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"cc-1"}'
            }
            started() {
              echo '{"type":"assistant","message":{"id":"msg_'$n'","role":"assistant","content":[{"type":"tool_use","id":"toolu_bg'$n'","name":"Bash","input":{"command":"'"$1"'","run_in_background":true}}]},"parent_tool_use_id":null}'
              echo '{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"task-'$n'","task_type":"local_bash","description":"'"$1"'"}],"session_id":"cc-1"}'
              echo '{"type":"system","subtype":"task_started","task_id":"task-'$n'","tool_use_id":"toolu_bg'$n'","description":"'"$1"'","is_backgrounded":true,"task_type":"local_bash","session_id":"cc-1"}'
              echo '{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_bg'$n'","type":"tool_result","content":"Started task-'$n'"}]},"parent_tool_use_id":null}'
              echo '{"type":"assistant","message":{"id":"msg_'$n'_b","role":"assistant","content":[{"type":"text","text":"Started it."}]},"parent_tool_use_id":null}'
              echo '{"type":"result","subtype":"success","is_error":false,"result":"Started it.","session_id":"cc-1"}'
            }
            while IFS= read -r line; do
              printf '%s\n' "$line" >> "$dir/stdin.txt"
              rid=$(printf '%s' "$line" | sed -n 's/.*"request_id":"\([^"]*\)".*/\1/p')
              case "$line" in
                *'"subtype":"interrupt"'*)
            {{{interrupt}}}
                  ;;
                *'"subtype":"set_model"'*)
            {{{setModel}}}
                  ;;
                *'"subtype":"stop_task"'*)
                  task=$(printf '%s' "$line" | sed -n 's/.*"task_id":"\([^"]*\)".*/\1/p')
                  echo '{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"cc-1"}'
                  echo '{"type":"system","subtype":"task_notification","task_id":"'"$task"'","status":"stopped","summary":"sleep 600","session_id":"cc-1"}'
                  echo '{"type":"control_response","response":{"subtype":"success","request_id":"'"$rid"'"}}'
                  ;;
                *'"subtype":"set_permission_mode"'*'"bypassPermissions"'*)
                  echo '{"type":"control_response","response":{"subtype":"error","request_id":"'"$rid"'","error":"Cannot set permission mode to bypassPermissions because the session was not launched with --dangerously-skip-permissions"}}'
                  ;;
                *'"subtype":"set_permission_mode"'*|*'"subtype":"apply_flag_settings"'*)
                  echo '{"type":"control_response","response":{"subtype":"success","request_id":"'"$rid"'"}}'
                  ;;
                *'"type":"control_response"'*'"updatedInput"'*)
                  echo '{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_ask","type":"tool_result","content":"Your questions have been answered"}]},"parent_tool_use_id":null}'
                  echo '{"type":"assistant","message":{"id":"msg_answered","role":"assistant","content":[{"type":"text","text":"You answered."}]},"parent_tool_use_id":null}'
                  echo '{"type":"result","subtype":"success","is_error":false,"result":"You answered.","session_id":"cc-1"}'
                  ;;
                *'"type":"user"'*)
                  if [ "$steering" = 1 ]; then
                    # Read at the step's end, in the same turn: no init of its own.
                    steering=0
                    echo '{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_steer","type":"tool_result","content":"one"}]},"parent_tool_use_id":null}'
                    echo '{"type":"assistant","message":{"id":"msg_steered","role":"assistant","content":[{"type":"text","text":"Changed course."}]},"parent_tool_use_id":null}'
                    echo '{"type":"result","subtype":"success","is_error":false,"result":"Changed course.","session_id":"cc-1"}'
                    continue
                  fi
                  n=$((n+1))
                  echo '{"type":"system","subtype":"init","session_id":"cc-1","model":"claude-test"}'
                  case "$line" in
                    *'start background'*) started "sleep 1"; wake 1 $n 0 & ;;
                    *'start slow background'*) started "sleep 2"; wake 0.2 $n 1.5 & ;;
                    *'start lasting background'*) started "sleep 600" ;;
                    *'steer me'*)
                      steering=1
                      echo '{"type":"assistant","message":{"id":"msg_'$n'","role":"assistant","content":[{"type":"tool_use","id":"toolu_steer","name":"Bash","input":{"command":"sleep 5; echo one"}}]},"parent_tool_use_id":null}'
                      ;;
                    *'ask me'*)
                      echo '{"type":"assistant","message":{"id":"msg_'$n'","role":"assistant","content":[{"type":"tool_use","id":"toolu_ask","name":"AskUserQuestion","input":{"questions":[{"question":"Which colour?","header":"Colour","options":[{"label":"Red","description":"r"},{"label":"Blue","description":"b"}],"multiSelect":false}]}}]},"parent_tool_use_id":null}'
                      echo '{"type":"control_request","request_id":"req_ask","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which colour?","header":"Colour","options":[{"label":"Red","description":"r"},{"label":"Blue","description":"b"}],"multiSelect":false}]},"tool_use_id":"toolu_ask"}}'
                      ;;
                    *'"hang"'*)
                      echo '{"type":"assistant","message":{"id":"msg_'$n'","role":"assistant","content":[{"type":"tool_use","id":"toolu_hang","name":"Bash","input":{"command":"sleep 600"}}]},"parent_tool_use_id":null}'
                      ;;
                    *)
                      echo '{"type":"assistant","message":{"id":"msg_'$n'","role":"assistant","content":[{"type":"text","text":"Reply '$n'"}]},"parent_tool_use_id":null}'
                      echo '{"type":"result","subtype":"success","is_error":false,"result":"Reply '$n'","session_id":"cc-1"}'
                      ;;
                  esac
                  ;;
              esac
            done
            """;
        File.WriteAllText(claude, script.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return claude;
    }

    private sealed class CapturingLogger : ILogger<ClaudeCodeHarnessSession>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
    }
}
