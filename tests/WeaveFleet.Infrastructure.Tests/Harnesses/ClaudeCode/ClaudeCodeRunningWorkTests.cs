using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Recorded Claude Code output (Claude Code 2.1.289) through <see cref="ClaudeCodeHarnessSession"/>'s pump: the running
/// work it reports, where a subagent's steps go, and what a command's output reads as.
/// </summary>
#pragma warning disable CA1001 // Disposal handled by IAsyncLifetime.DisposeAsync
public sealed class ClaudeCodeRunningWorkTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string SessionId = "fleet-cc-work";
    private const string NestingCall = "toolu_01245FoVP8TYZZmiBHS9xNuL";
    private const string BackgroundCall = "toolu_01FnJ9zVuSCUQy5ZP83n3kfM";
    private const string NestedCall = "toolu_01AKuTh7w6uzqddngQVJ8VN5";

    private readonly InMemoryMessageRepository _messages = new();
    private readonly ConcurrentQueue<(string Parent, string Call, string Title)> _children = new();
    private readonly string _tasks = Directory.CreateTempSubdirectory("fleet-cc-tasks-").FullName;
    private readonly ClaudeCodeHarnessSession _session;

    public ClaudeCodeRunningWorkTests() => _session = NewSession();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        Directory.Delete(_tasks, recursive: true);
    }

    [Fact]
    public async Task Subagents_steps_go_to_child_sessions_nested_under_the_conversation_that_called_them()
    {
        var events = await PumpAsync(Fixture("subagents.jsonl"));

        // One child session per subagent, under the conversation that made the call: the nested one under its caller's.
        _children.ShouldBe(
        [
            (SessionId, NestingCall, "Nested agent spawn test"),
            (SessionId, BackgroundCall, "Background sleep and print test"),
            (Child(NestingCall), NestedCall, "Run nested echo command"),
        ], ignoreOrder: true);

        // The nested subagent: what it was asked, its Bash call with the result, and its reply, on its model.
        var nested = Messages(Child(NestedCall));
        nested.First().Role.ShouldBe("user");
        nested.First().TextContent.ShouldBe("Run the Bash command `echo nested-ok` and reply with its output.");
        var bash = nested.SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldHaveSingleItem();
        (bash.ToolName, bash.State).ShouldBe(("bash", ToolUseState.Completed));
        nested.SelectMany(m => m.Parts).OfType<ToolResultPart>().ShouldHaveSingleItem().Content.ShouldBe("nested-ok");
        nested.Last().TextContent.ShouldContain("nested-ok");
        nested.Where(m => m.Role == "assistant").ShouldAllBe(m => m.ModelId == "claude-haiku-4-5-20251001");

        // Its caller's conversation has the call that started it; the session's own has neither's steps.
        Messages(Child(NestingCall)).SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldHaveSingleItem().ToolCallId.ShouldBe(NestedCall);
        Messages(Child(BackgroundCall)).Last().TextContent.ShouldBe("DONE-BG");

        // A foreground subagent's conversation starts with its prompt as a user line too; it's kept once.
        Messages(Child(NestingCall)).Count(m => m.Role == "user").ShouldBe(1);
        Messages(Child(BackgroundCall)).First().TextContent.ShouldStartWith("Run the Bash command `python3");
        var root = Messages(SessionId);
        root.SelectMany(m => m.Parts).OfType<ToolUsePart>().Select(t => t.ToolCallId).ShouldBe([NestingCall, BackgroundCall]);
        root.SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldAllBe(t => t.State == ToolUseState.Completed);
        root.ShouldNotContain(m => m.TextContent.Contains("DONE-BG", StringComparison.Ordinal) && m.Role == "user");

        // The nested subagent's work is reported on its caller's child session; the rest on the session.
        var started = Work(events, EventTypes.WorkStarted);
        started.Single(e => Report(e).WorkId == "a382794824e648d73").FleetSessionId.ShouldBe(Child(NestingCall));
        started.Where(e => Report(e).WorkId != "a382794824e648d73").ShouldAllBe(e => e.FleetSessionId == null);
        started.Select(e => Report(e).ChildHarnessSessionId).ShouldBe([NestingCall, BackgroundCall, NestedCall, null]);
        Work(events, EventTypes.WorkEnded).Select(e => Report(e).EndedReason).ShouldAllBe(r => r == WorkEndedReasons.Completed);

        // Each child session reads as working while its subagent runs.
        foreach (var child in new[] { NestingCall, BackgroundCall, NestedCall }.Select(Child))
            Statuses(events, child).ShouldBe(["busy", "idle"]);
        // The session's own turns: the prompt's, then three Claude Code started as its subagents and their work finished.
        Statuses(events, SessionId).ShouldBe(["busy", "idle", "busy", "idle", "busy", "idle", "busy", "idle"]);
    }

    [Fact]
    public async Task A_child_session_that_cant_be_made_drops_the_subagents_steps_and_keeps_the_conversation()
    {
        await using var session = NewSession(children: (_, _, _) => Task.FromResult<string?>(null));
        var events = await PumpAsync(session, Fixture("subagents.jsonl"));

        _messages.All.ShouldAllBe(m => m.SessionId == SessionId);
        Work(events, EventTypes.WorkStarted).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Background_commands_and_monitors_are_reported_and_end_with_their_exit_code()
    {
        var events = await PumpAsync(Fixture("background-shell-and-monitor.jsonl"));

        Work(events, EventTypes.WorkStarted).Select(e => (Report(e).Kind, Report(e).Title)).ShouldBe([("shell", "Bash"), ("monitor", "Monitor")]);
        Work(events, EventTypes.WorkEnded).Select(e => (Report(e).WorkId, Report(e).Detail)).ShouldBe([("bvu3hkhyn", null), ("b51djw65i", "exit 0")]);
        events.ShouldAllBe(e => !EventTypes.IsWorkEvent(e.Type) || e.FleetSessionId == null);
    }

    [Fact]
    public async Task A_commands_output_is_read_from_its_file_a_page_at_a_time()
    {
        await File.WriteAllTextAsync(Path.Combine(_tasks, "b51djw65i.output"), "tick1\ntock\n\n[exited with code 0]\n");
        await File.WriteAllTextAsync(Path.Combine(_tasks, "bvu3hkhyn.output"), "line0\nline1\nline2\n");
        await PumpAsync(Fixture("background-shell-and-monitor.jsonl"));

        var shell = await _session.ReadWorkOutputAsync("b51djw65i", 0, CancellationToken.None);
        shell.ShouldBe(new WorkOutput("tick1\ntock\n\n[exited with code 0]\n", 33, 33, false));

        // The monitor's file is never named; it's next to the command's.
        var monitor = await _session.ReadWorkOutputAsync("bvu3hkhyn", 6, CancellationToken.None);
        monitor.ShouldBe(new WorkOutput("line1\nline2\n", 18, 18, false));
        (await _session.ReadWorkOutputAsync("bvu3hkhyn", 18, CancellationToken.None))!.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task Work_still_running_when_the_process_ends_is_lost()
    {
        // Up to the first turn's result: the command and the monitor still run, and then the process is gone.
        var lines = Fixture("background-shell-and-monitor.jsonl");
        var events = await PumpAsync(lines.TakeWhile(line => !line.Contains("\"type\": \"result\"", StringComparison.Ordinal)).ToArray());

        Work(events, EventTypes.WorkEnded).Select(e => (Report(e).WorkId, Report(e).EndedReason))
            .ShouldBe([("b51djw65i", WorkEndedReasons.Lost), ("bvu3hkhyn", WorkEndedReasons.Lost)], ignoreOrder: true);
        (await _session.GetRunningWorkAsync(CancellationToken.None)).ShouldBeEmpty();
        _session.BackgroundWork.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_subagent_cut_off_by_its_process_ends_lost_and_its_child_stops_working()
    {
        var lines = Fixture("subagents.jsonl");
        var cut = Array.FindIndex(lines, line => line.Contains("\"task_progress\"", StringComparison.Ordinal));
        var events = await PumpAsync(lines.Take(cut).ToArray());

        Work(events, EventTypes.WorkEnded).Select(e => Report(e).EndedReason).Distinct().ShouldBe([WorkEndedReasons.Lost]);
        Statuses(events, Child(NestingCall)).ShouldBe(["busy", "idle"]);
    }

    [Fact]
    public async Task A_subagents_child_session_cant_be_prompted_and_has_no_work_of_its_own()
    {
        await using var child = NewSession(readOnlyChild: true);

        var prompt = () => child.SendPromptAsync("hello", null, CancellationToken.None);
        (await prompt.ShouldThrowAsync<InvalidOperationException>()).Message.ShouldContain("subagent");
        (await child.GetRunningWorkAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void A_page_never_ends_inside_a_character()
    {
        var file = Path.Combine(_tasks, "utf8.output");
        var text = new string('a', ClaudeCodeTaskOutput.PageBytes - 1) + "é tail";
        File.WriteAllText(file, text);

        var first = ClaudeCodeTaskOutput.Read(file, 0);
        first.Text.ShouldBe(new string('a', ClaudeCodeTaskOutput.PageBytes - 1));
        first.NextOffset.ShouldBe(ClaudeCodeTaskOutput.PageBytes - 1);
        ClaudeCodeTaskOutput.Read(file, first.NextOffset).Text.ShouldBe("é tail");
        ClaudeCodeTaskOutput.Read(Path.Combine(_tasks, "missing.output"), 0).ShouldBe(new WorkOutput("", 0, 0, false));
    }

    [Fact]
    public void Claudes_own_tail_of_the_output_pages_from_the_offset_asked_for()
    {
        using var tail = JsonDocument.Parse("""{"output":"line1\nline2\n","total_bytes":18,"truncated":true}""");

        ClaudeCodeTaskOutput.FromTail(tail.RootElement, 12).ShouldBe(new WorkOutput("line2\n", 18, 18, false));
        ClaudeCodeTaskOutput.FromTail(tail.RootElement, 0).ShouldBe(new WorkOutput("line1\nline2\n", 18, 18, true));
        ClaudeCodeTaskOutput.FromTail(tail.RootElement, 18)!.Text.ShouldBeEmpty();
    }

    [Fact]
    public void A_subagents_prompt_reads_as_a_string_or_as_blocks()
    {
        var line = """{"type":"user","message":{"role":"user","content":"Run it."},"parent_tool_use_id":"toolu_1"}""";
        var message = JsonSerializer.Deserialize(line, ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage)
            .ShouldBeOfType<ClaudeCodeUserMessage>();
        message.Message!.Content.ShouldHaveSingleItem().ShouldBeOfType<ClaudeCodeTextBlock>().Text.ShouldBe("Run it.");
    }

    // -----------------------------------------------------------------------

    private static string Child(string call) => $"child-{call}";

    private ClaudeCodeHarnessSession NewSession(Func<string, string, string, Task<string?>>? children = null, bool readOnlyChild = false)
    {
        var delegations = new InMemoryDelegationRepository();
        var sessions = new InMemorySessionRepository();
        var connections = new FakeDbConnectionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(_messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, _messages, delegations, sessions, new InMemorySmartLinkRepository(), new InMemoryOutboxRepository(), new FakeOutboxDispatcher()));

        return new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: SessionId,
            workingDirectory: "/tmp",
            config: new ClaudeCodeOptions { BinaryPath = "/nonexistent/claude" },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            readOnlyChild: readOnlyChild)
        {
            ChildSessions = children ?? ((parent, call, title) =>
            {
                _children.Enqueue((parent, call, title));
                return Task.FromResult<string?>(Child(call));
            }),
        };
    }

    private string[] Fixture(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", name))
            .Select(line => line.Replace("<TASKS>", _tasks, StringComparison.Ordinal))
            .ToArray();

    private Task<List<HarnessEvent>> PumpAsync(params string[] lines) => PumpAsync(_session, lines);

    /// <summary>Runs the pump over <paramref name="lines"/> to the end of the process, and returns the events it published.</summary>
    private static async Task<List<HarnessEvent>> PumpAsync(ClaudeCodeHarnessSession session, string[] lines)
    {
        var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")));
        await using (var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance))
            await session.PumpStdoutAsync(stdout, process);

        await session.StopAsync(CancellationToken.None);
        var events = new List<HarnessEvent>();
        await foreach (var evt in session.SubscribeAsync(CancellationToken.None))
            events.Add(evt);
        return events;
    }

    private List<HarnessMessage> Messages(string sessionId)
        => MessagePersistenceService.ToHarnessMessages(
                _messages.All.Where(m => m.SessionId == sessionId).OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            .ToList();

    private static List<HarnessEvent> Work(List<HarnessEvent> events, string type) => events.Where(e => e.Type == type).ToList();

    private static WorkReport Report(HarnessEvent evt) => JsonSerializer.Deserialize(evt.Payload!.Value, InfrastructureJsonContext.Default.WorkReport)!;

    /// <summary>A session's busy/idle story, in order.</summary>
    private static List<string> Statuses(List<HarnessEvent> events, string sessionId)
        => events
            .Where(e => (e.FleetSessionId ?? e.SessionId) == sessionId)
            .Select(e => e.Type == EventTypes.SessionIdle
                ? "idle"
                : e.Type == EventTypes.SessionStatus ? e.Payload!.Value.GetProperty("status").GetProperty("type").GetString() : null)
            .OfType<string>()
            .ToList();
}
