using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Feeds recorded <c>claude -p --output-format stream-json</c> output through
/// <see cref="ClaudeCodeHarnessSession"/>'s stdout pump and checks what the conversation ends up with.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class ClaudeCodeHarnessSessionPumpTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string SessionId = "fleet-cc-pump";

    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.Parse("2026-10-07T01:51:43Z", System.Globalization.CultureInfo.InvariantCulture);

    private readonly InMemoryMessageRepository _messages = new();
    private readonly InMemoryOutboxRepository _outbox = new();
    private readonly ClaudeCodeHarnessSession _session;

    public ClaudeCodeHarnessSessionPumpTests()
    {
        var delegations = new InMemoryDelegationRepository();
        var sessions = new InMemorySessionRepository();
        var dispatcher = new FakeOutboxDispatcher();
        var connections = new FakeDbConnectionFactory();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(_messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, _messages, delegations, sessions, new InMemorySmartLinkRepository(), _outbox, dispatcher));

        _session = new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: SessionId,
            workingDirectory: "/tmp",
            config: new ClaudeCodeOptions { BinaryPath = "/nonexistent/claude" },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId)
        {
            // When the limit fixtures were recorded, so their reset is still ahead.
            Time = new FakeTimeProvider(RecordedAt),
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _session.DisposeAsync();

    [Fact]
    public async Task TwoToolRun_SavesEachToolWithItsResult()
    {
        var events = await PumpAsync(Fixture("two-tools.jsonl"));

        var tools = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ToList();
        tools.Select(t => t.ToolName).ShouldBe(["read", "bash"]);
        tools.ShouldAllBe(t => t.State == ToolUseState.Completed);
        Conversation().Last().Parts.OfType<TextPart>().ShouldHaveSingleItem().Text
            .ShouldBe("The note file contains \"hello from file\" and the echo command executed successfully.");

        // The finished Bash call reaches the page with its output.
        var bashUpdate = _outbox.All
            .Where(m => m.Type == EventTypes.MessagePartUpdated)
            .Select(m => JsonDocument.Parse(m.Payload).RootElement.GetProperty("part"))
            .Last(p => p.TryGetProperty("tool", out var name) && name.GetString() == "bash");
        bashUpdate.GetProperty("state").GetProperty("status").GetString().ShouldBe("completed");
        bashUpdate.GetProperty("state").GetProperty("output").GetString().ShouldBe("done");

        AllText().ShouldNotContain(t => t.StartsWith("Claude Code", StringComparison.Ordinal));
        events.Count(e => e.Type == EventTypes.SessionIdle).ShouldBe(1);
    }

    [Fact]
    public async Task BackgroundWake_ShowsTheTurnClaudeStartedByItselfLikeAnyOther()
    {
        // Recorded: a prompt starts a background command and its turn ends; when the command finishes, Claude Code
        // goes on with no prompt (a new init, then output); then the host's next prompt.
        var events = await PumpAsync(Fixture("background-wake.jsonl"));

        AllText().ShouldBe(["STARTED", "The background task completed successfully with output \"bgdone\".", "SECOND"]);
        var statuses = events
            .Where(e => e.Type is EventTypes.SessionIdle or EventTypes.SessionStatus)
            .Select(e => e.Type == EventTypes.SessionIdle ? "idle" : e.Payload!.Value.GetProperty("status").GetProperty("type").GetString())
            .ToList();
        statuses.ShouldBe(["busy", "idle", "busy", "idle", "busy", "idle"]);
        _session.BackgroundWork.ShouldBeEmpty();
        AllText().ShouldNotContain(t => t.StartsWith("Claude Code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackgroundWorkStarted_IsTrackedUntilClaudeSaysItEnded()
    {
        var lines = Fixture("background-wake.jsonl");
        var pump = new System.IO.Pipelines.Pipe();
        await using var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance);

        // Up to the first result: the command still runs.
        await using (var writer = pump.Writer.AsStream())
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\n", lines.Take(7)) + "\n"));
            var reading = _session.PumpStdoutAsync(new StreamReader(pump.Reader.AsStream()), process);
            await WaitForAsync(() => _session.BackgroundWork.Count == 1);
            _session.BackgroundWork.ShouldBe(["bknxvrqqv"]);

            // Its notification ends it.
            await writer.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\n", lines.Skip(7).Take(3)) + "\n"));
            await WaitForAsync(() => _session.BackgroundWork.Count == 0);
            await pump.Writer.CompleteAsync();
            await reading;
        }
    }

    [Fact]
    public async Task TwoToolRun_GivesMessagesIdsThatSortInTheOrderTheyArrived()
    {
        await PumpAsync(Fixture("two-tools.jsonl"));

        // Claude's own ids ("msg_011C…") don't sort by time, and the page orders messages by id.
        var conversation = Conversation();
        conversation.Count.ShouldBe(3);
        conversation.ShouldAllBe(m => !m.Id.StartsWith("msg_011C", StringComparison.Ordinal));
        conversation.Select(m => m.Id).ShouldBe(conversation.Select(m => m.Id).Order(StringComparer.Ordinal));
        conversation.Last().Parts.ShouldHaveSingleItem().ShouldBeOfType<TextPart>();
    }

    [Fact]
    public async Task TextThenToolInOneMessage_KeepsBoth()
    {
        // Claude Code writes one line per content block; the tool line used to replace the text.
        await PumpAsync(
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"text","text":"Let me look."}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"note.txt"}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"hello"}]},"parent_tool_use_id":null}""",
            """{"subtype":"success","is_error":false,"result":"","session_id":"s","type":"result"}""");

        var message = Conversation().ShouldHaveSingleItem();
        message.Parts.OfType<TextPart>().ShouldHaveSingleItem().Text.ShouldBe("Let me look.");
        message.Parts.OfType<ToolUsePart>().ShouldHaveSingleItem().State.ShouldBe(ToolUseState.Completed);

        // Kept next to the call, where a reloaded session reads the tool's output from.
        var result = message.Parts.OfType<ToolResultPart>().ShouldHaveSingleItem();
        result.ToolCallId.ShouldBe("toolu_1");
        result.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task FailedTool_IsSavedAsAnError()
    {
        await PumpAsync(
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"false"}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Exit code 1","is_error":true}]},"parent_tool_use_id":null}""",
            """{"subtype":"success","is_error":false,"result":"","session_id":"s","type":"result"}""");

        var tool = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldHaveSingleItem();
        tool.State.ShouldBe(ToolUseState.Error);
        tool.Error.ShouldBe("Exit code 1");
    }

    [Fact]
    public async Task SubAgentSteps_StayOutOfTheConversation()
    {
        await PumpAsync(
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_task","name":"Agent","input":{}}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_B","role":"assistant","content":[{"type":"tool_use","id":"toolu_inner","name":"Read","input":{}}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_inner","type":"tool_result","content":"x"}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_task","type":"tool_result","content":"sub-agent done"}]},"parent_tool_use_id":null}""",
            """{"subtype":"success","is_error":false,"result":"","session_id":"s","type":"result"}""");

        var tool = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldHaveSingleItem();
        // Fleet's subagent tool, so the call links to its child session.
        tool.ToolName.ShouldBe("task");
        tool.State.ShouldBe(ToolUseState.Completed);
    }

    [Fact]
    public async Task MaxTurns_SaysWhyItStopped()
    {
        await PumpAsync(Fixture("max-turns.jsonl"));

        AllText().ShouldContain("Claude Code stopped: Reached maximum number of turns (1)");
    }

    [Fact]
    public async Task NotLoggedIn_ShowsClaudesMessageOnce()
    {
        await PumpAsync(Fixture("not-logged-in.jsonl"));

        AllText().ShouldBe(["Not logged in · Please run /login"]);
    }

    [Fact]
    public async Task UsageLimit_IsAFailure_WithTheWindowsReset_ReportedBeforeTheIdle()
    {
        var events = await PumpAsync(Fixture("usage-limit.jsonl"));

        var failure = Failure(events);
        failure.Kind.ShouldBe(TurnErrorKinds.UsageLimit);
        failure.RetryAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791344624));
        failure.Message.ShouldBe("You've hit your session limit · resets 3:43am (UTC)");
        events.FindIndex(e => e.Type == EventTypes.SessionError).ShouldBeLessThan(events.FindIndex(e => e.Type == EventTypes.SessionIdle));

        // The failure card says it; Claude Code's own line doesn't say it twice.
        AllText().ShouldBeEmpty();
    }

    [Fact]
    public async Task RateLimitWithoutAWindow_IsARateLimit_WithNoResetTime()
    {
        var events = await PumpAsync(Fixture("rate-limit.jsonl"));

        var failure = Failure(events);
        failure.Kind.ShouldBe(TurnErrorKinds.RateLimit);
        failure.RetryAt.ShouldBeNull();
        failure.Message.ShouldStartWith("API Error: Server is temporarily limiting requests");
    }

    [Fact]
    public async Task Overloaded_IsAnOverload()
    {
        var events = await PumpAsync(Fixture("overloaded.jsonl"));

        Failure(events).Kind.ShouldBe(TurnErrorKinds.Overloaded);
        AllText().ShouldBeEmpty();
    }

    [Fact]
    public async Task ServerErrorThatIsntALimit_KeepsClaudesOwnWords()
    {
        var events = await PumpAsync(
            """{"type":"system","subtype":"init","session_id":"cc-1","model":"claude-haiku-4-5"}""",
            """{"type":"assistant","message":{"id":"e-1","model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"API Error: 500 Internal server error"}]},"parent_tool_use_id":null,"error":"server_error","is_api_error_message":true}""",
            """{"type":"result","subtype":"success","is_error":true,"api_error_status":500,"terminal_reason":"api_error","result":"API Error: 500 Internal server error","session_id":"cc-1"}""");

        events.ShouldNotContain(e => e.Type == EventTypes.SessionError);
        AllText().ShouldBe(["API Error: 500 Internal server error"]);
    }

    [Fact]
    public async Task ARejectedWindow_IsntTheReasonForA529()
    {
        var events = await PumpAsync(
            """{"type":"system","subtype":"init","session_id":"cc-1","model":"claude-haiku-4-5"}""",
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1791344624,"rateLimitType":"five_hour"}}""",
            """{"type":"assistant","message":{"id":"e-1","model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"API Error: 529 Overloaded."}]},"parent_tool_use_id":null,"error":"server_error","is_api_error_message":true}""",
            """{"type":"result","subtype":"success","is_error":true,"api_error_status":529,"terminal_reason":"api_error","result":"API Error: 529 Overloaded.","session_id":"cc-1"}""");

        var failure = Failure(events);
        failure.Kind.ShouldBe(TurnErrorKinds.Overloaded);
        failure.RetryAt.ShouldBeNull();
    }

    [Fact]
    public async Task RunWithoutResult_StopsItsToolsAndSaysSo()
    {
        var events = await PumpAsync(
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"sleep 60"}}]},"parent_tool_use_id":null}""");

        var tool = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldHaveSingleItem();
        tool.State.ShouldBe(ToolUseState.Error);
        tool.Error.ShouldBe("Stopped before it finished.");
        AllText().ShouldContain("Claude Code stopped before finishing.");

        // Nothing else will say the turn is over.
        events.Count(e => e.Type == EventTypes.SessionIdle).ShouldBe(1);
    }

    [Fact]
    public async Task TwoToolRun_ReportsEachCallsSizeOnce_AndTheLastCallsRealOutputAtTheEnd()
    {
        var events = await PumpAsync(Fixture("two-tools.jsonl"));

        // Claude Code repeats a call's usage on each of its lines; the result's last iteration has the real output.
        var calls = ContextReports(events).Select(r => r.Call.ShouldNotBeNull()).ToList();
        calls.Select(c => c.CacheRead).ShouldBe([12224, 19370, 19608, 19608]);
        calls[^1].Output.ShouldBe(60);
        calls[^1].Used.ShouldBe(8 + 19608 + 150 + 60);
        ContextReports(events).ShouldAllBe(r => r.ModelId != null && r.ProviderId == "anthropic");
    }

    [Fact]
    public async Task ResultLine_GivesTheModelsWindow_AndWhereClaudeCodeCompacts()
    {
        var events = await PumpAsync(
            """{"type":"system","subtype":"init","session_id":"cc-1","model":"claude-opus-5"}""",
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":4,"output_tokens":1,"cache_read_input_tokens":30000,"cache_creation_input_tokens":200}},"parent_tool_use_id":null}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"hi","session_id":"cc-1","usage":{"input_tokens":4,"output_tokens":12,"cache_read_input_tokens":30000,"cache_creation_input_tokens":200,"iterations":[{"type":"message","input_tokens":4,"output_tokens":12,"cache_read_input_tokens":30000,"cache_creation_input_tokens":200}]},"modelUsage":{"claude-opus-5":{"inputTokens":4,"outputTokens":12,"contextWindow":1000000,"maxOutputTokens":64000,"costUSD":0.01}}}""");

        var reports = ContextReports(events);
        reports[0].Limit.ShouldBeNull();
        var final = reports[^1];
        final.Limit.ShouldBe(1_000_000);
        final.CompactsAt.ShouldBe(1_000_000 - 20_000 - 13_000);
        final.ModelId.ShouldBe("claude-opus-5");
        final.Call.ShouldNotBeNull().Used.ShouldBe(30_216);
    }

    [Fact]
    public async Task CompactionLines_StartEndAndFailACompaction()
    {
        var events = await PumpAsync(
            """{"type":"system","subtype":"status","status":"compacting","session_id":"cc-1"}""",
            """{"type":"system","subtype":"compact_boundary","session_id":"cc-1","compact_metadata":{"trigger":"manual","pre_tokens":120000}}""",
            """{"type":"system","subtype":"status","status":"compacting","session_id":"cc-1"}""",
            """{"type":"system","subtype":"status","status":null,"compact_result":"failed","compact_error":"Not enough messages to compact.","session_id":"cc-1"}""");

        var compactions = events.Where(e => e.Type == EventTypes.ContextCompaction).Select(e => ContextEvents.ReadCompaction(e).ShouldNotBeNull()).ToList();
        compactions.Select(c => c.Phase).ShouldBe([ContextCompactionPhases.Started, ContextCompactionPhases.Ended, ContextCompactionPhases.Started, ContextCompactionPhases.Failed]);
        compactions[1].Trigger.ShouldBe(ContextCompactionTriggers.Manual);
        compactions[3].Error.ShouldBe("Not enough messages to compact.");
    }

    private static List<ContextUsageReport> ContextReports(IEnumerable<HarnessEvent> events)
        => events.Where(e => e.Type == EventTypes.ContextUsage).Select(e => ContextEvents.ReadUsage(e).ShouldNotBeNull()).ToList();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never held.");
            await Task.Delay(10);
        }
    }

    private static string[] Fixture(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", name));

    /// <summary>Runs the pump over <paramref name="lines"/> and returns the events it published.</summary>
    private async Task<List<HarnessEvent>> PumpAsync(params string[] lines)
    {
        var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")));
        await using (var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance))
            await _session.PumpStdoutAsync(stdout, process);

        await _session.StopAsync(CancellationToken.None);
        var events = new List<HarnessEvent>();
        await foreach (var evt in _session.SubscribeAsync(CancellationToken.None))
            events.Add(evt);

        return events;
    }

    /// <summary>The assistant messages as the page reads them back, oldest first.</summary>
    private List<HarnessMessage> Conversation()
        => MessagePersistenceService.ToHarnessMessages(
                _messages.All.Where(m => m.Role == "assistant").OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            .ToList();

    private static TurnError Failure(List<HarnessEvent> events)
        => HarnessErrorReader.TryReadFromPayload(events.Single(e => e.Type == EventTypes.SessionError).Payload, RecordedAt).ShouldNotBeNull();

    private List<string> AllText()
        => Conversation().SelectMany(m => m.Parts).OfType<TextPart>().Select(t => t.Text).ToList();
}
