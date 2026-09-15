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
/// Feeds recorded <c>claude -p --output-format stream-json</c> output through
/// <see cref="ClaudeCodeHarnessSession"/>'s stdout pump and checks what the conversation ends up with.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class ClaudeCodeHarnessSessionPumpTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string SessionId = "fleet-cc-pump";

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
            ownerUserId: TestUserContext.DefaultUserId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _session.DisposeAsync();

    [Fact]
    public async Task TwoToolRun_SavesEachToolWithItsResult()
    {
        var events = await PumpAsync(Fixture("two-tools.jsonl"));

        var tools = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ToList();
        tools.Select(t => t.ToolName).ShouldBe(["Read", "Bash"]);
        tools.ShouldAllBe(t => t.State == ToolUseState.Completed);
        Conversation().Last().Parts.OfType<TextPart>().ShouldHaveSingleItem().Text
            .ShouldBe("The note file contains \"hello from file\" and the echo command executed successfully.");

        // The finished Bash call reaches the page with its output.
        var bashUpdate = _outbox.All
            .Where(m => m.Type == EventTypes.MessagePartUpdated)
            .Select(m => JsonDocument.Parse(m.Payload).RootElement.GetProperty("part"))
            .Last(p => p.TryGetProperty("tool", out var name) && name.GetString() == "Bash");
        bashUpdate.GetProperty("state").GetProperty("status").GetString().ShouldBe("completed");
        bashUpdate.GetProperty("state").GetProperty("output").GetString().ShouldBe("done");

        AllText().ShouldNotContain(t => t.StartsWith("Claude Code", StringComparison.Ordinal));
        events.Count(e => e.Type == EventTypes.SessionIdle).ShouldBe(1);
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
        tool.ToolName.ShouldBe("Agent");
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

    private List<string> AllText()
        => Conversation().SelectMany(m => m.Parts).OfType<TextPart>().Select(t => t.Text).ToList();
}
