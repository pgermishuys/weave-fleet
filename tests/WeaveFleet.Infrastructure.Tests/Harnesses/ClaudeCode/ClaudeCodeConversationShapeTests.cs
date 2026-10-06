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
/// What recorded Claude Code 2.1.290 output (<c>Fixtures/ClaudeCode</c>) looks like in Fleet's conversation: text that
/// streams in as the model writes it, tool calls under the names and inputs Fleet shows OpenCode's, an edit's diff, and
/// <c>AskUserQuestion</c> as Fleet's question.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class ClaudeCodeConversationShapeTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string SessionId = "fleet-cc-shape";

    private readonly InMemoryMessageRepository _messages = new();
    private readonly InMemoryOutboxRepository _outbox = new();
    private readonly ClaudeCodeHarnessSession _session;

    public ClaudeCodeConversationShapeTests()
    {
        var delegations = new InMemoryDelegationRepository();
        var sessions = new InMemorySessionRepository();
        var connections = new FakeDbConnectionFactory();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(_messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, _messages, delegations, sessions, new InMemorySmartLinkRepository(), _outbox, new FakeOutboxDispatcher()));

        _session = new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: SessionId,
            workingDirectory: "/work",
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
    public async Task Text_streams_in_as_deltas_under_the_ids_its_saved_part_gets()
    {
        var events = await PumpAsync(Fixture("partial-messages.jsonl"));

        var deltas = events.Where(e => e.Type == EventTypes.MessagePartDelta).Select(Delta).ToList();
        deltas.Count.ShouldBeGreaterThan(10);

        // The first text block: its deltas build exactly what is saved, under the saved message and part.
        var reply = Conversation().First(m => m.Parts.OfType<TextPart>().Any());
        var saved = reply.Parts.OfType<TextPart>().First();
        var streamed = deltas.Where(d => d.PartId == saved.PartId).ToList();
        streamed.ShouldNotBeEmpty();
        streamed.ShouldAllBe(d => d.MessageId == reply.Id);
        string.Concat(streamed.Select(d => d.Text)).ShouldBe(saved.Text);
        saved.Text.ShouldStartWith("Lighthouses have");

        // Saved once: the finished block fills the part the deltas named, it doesn't add another.
        reply.Parts.OfType<TextPart>().Count().ShouldBe(1);

        // The part opens empty before its first delta, and the finished part follows the last one on the same way.
        var forPart = events.Select((e, i) => (e, i)).Where(x => PartOf(x.e) == saved.PartId).ToList();
        forPart.First().e.Type.ShouldBe(EventTypes.MessagePartUpdated);
        forPart.Last().e.Type.ShouldBe(EventTypes.MessagePartUpdated);
        forPart.Last().e.Payload!.Value.GetProperty("part").GetProperty("text").GetString().ShouldBe(saved.Text);
    }

    [Fact]
    public async Task Thinking_with_no_text_streams_nothing_and_saves_nothing()
    {
        var events = await PumpAsync(Fixture("partial-messages.jsonl"));

        // Claude Code 2.1.290 sends haiku's thinking as empty deltas (an estimate only): nothing to show.
        Conversation().SelectMany(m => m.Parts).OfType<ReasoningPart>().ShouldBeEmpty();
        events.Where(e => e.Type == EventTypes.MessagePartUpdated)
            .ShouldNotContain(e => e.Payload!.Value.GetProperty("part").GetProperty("type").GetString() == "reasoning");
    }

    [Fact]
    public async Task File_tools_get_Fleets_names_and_inputs_and_an_edit_its_diff()
    {
        await PumpAsync(Fixture("file-tools.jsonl"));

        var tools = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ToList();
        tools.Select(t => t.ToolName).ShouldBe(["read", "edit", "edit", "edit", "write", "bash", "bash", "TaskCreate", "TaskCreate", "bash"]);

        var read = tools[0];
        read.Arguments.GetProperty("filePath").GetString().ShouldBe("/work/calc.py");
        read.Arguments.TryGetProperty("file_path", out _).ShouldBeFalse();

        // The edit: OpenCode's input names, and the patch Claude Code reported, with its real line numbers.
        var edit = tools[1];
        edit.State.ShouldBe(ToolUseState.Completed);
        edit.Arguments.GetProperty("oldString").GetString().ShouldBe("def sub(a, b):");
        edit.Arguments.GetProperty("newString").GetString().ShouldBe("def subtract(a, b):");
        edit.Arguments.GetProperty("replaceAll").GetBoolean().ShouldBeFalse();
        var diff = edit.Metadata!.Value.GetProperty("diff").GetString()!;
        diff.ShouldStartWith("--- /work/calc.py\n+++ /work/calc.py\n@@ -2,");
        diff.ShouldContain("\n-def sub(a, b):\n+def subtract(a, b):\n");

        // A new file: every line added.
        var write = tools[4];
        write.Arguments.GetProperty("filePath").GetString().ShouldBe("/work/notes.txt");
        write.Metadata!.Value.GetProperty("diff").GetString().ShouldBe("--- /dev/null\n+++ /work/notes.txt\n@@ -0,0 +1,2 @@\n+alpha\n+beta\n");

        // Bash keeps what it ran and why.
        tools[9].Arguments.GetProperty("command").GetString().ShouldBe("git status --short");
        tools[9].Arguments.GetProperty("description").GetString().ShouldBe("Show status");
    }

    [Fact]
    public async Task The_diff_survives_a_reload_as_the_saved_events_carry_it()
    {
        await PumpAsync(Fixture("file-tools.jsonl"));

        // What a client that missed the live events reads: the saved message.part.updated for the first edit.
        var callId = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().First(t => t.ToolName == "edit").ToolCallId;
        var editEvent = _outbox.All
            .Where(o => o.Type == EventTypes.MessagePartUpdated)
            .Select(o => JsonDocument.Parse(o.Payload).RootElement.GetProperty("part"))
            .Last(p => p.TryGetProperty("callID", out var call) && call.GetString() == callId);
        editEvent.GetProperty("state").GetProperty("status").GetString().ShouldBe("completed");
        editEvent.GetProperty("state").GetProperty("metadata").GetProperty("diff").GetString()!.ShouldContain("+def subtract(a, b):");
    }

    [Fact]
    public async Task AskUserQuestion_is_Fleets_question_and_waits_on_the_user_without_a_permission_ask()
    {
        var events = await PumpAsync(Fixture("ask-user-question.jsonl").TakeWhile(line => !line.Contains("\"tool_result\"")).ToArray());

        var question = Conversation().SelectMany(m => m.Parts).OfType<ToolUsePart>().ShouldHaveSingleItem();
        question.ToolName.ShouldBe("question");
        var asked = question.Arguments.GetProperty("questions");
        asked[0].GetProperty("question").GetString().ShouldBe("Which colour do you prefer?");
        asked[0].GetProperty("multiple").GetBoolean().ShouldBeFalse();
        asked[1].GetProperty("multiple").GetBoolean().ShouldBeTrue();
        asked[1].GetProperty("options").GetArrayLength().ShouldBe(3);

        events.ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);
        events.Where(e => e.Type == EventTypes.SessionStatus)
            .Select(e => e.Payload!.Value.GetProperty("status").GetProperty("type").GetString())
            .ShouldContain(ActivityStatuses.WaitingInput);
    }

    private static (string MessageId, string PartId, string Text) Delta(HarnessEvent evt)
    {
        var payload = evt.Payload!.Value;
        return (payload.GetProperty("messageID").GetString()!, payload.GetProperty("partID").GetString()!, payload.GetProperty("delta").GetString()!);
    }

    private static string? PartOf(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;
        if (evt.Type == EventTypes.MessagePartDelta)
            return payload.GetProperty("partID").GetString();
        return evt.Type == EventTypes.MessagePartUpdated && payload.TryGetProperty("part", out var part) ? part.GetProperty("id").GetString() : null;
    }

    private static string[] Fixture(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", name));

    /// <summary>Runs the pump over <paramref name="lines"/> and returns the events it published, in order.</summary>
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

    private List<HarnessMessage> Conversation()
        => MessagePersistenceService.ToHarnessMessages(
                _messages.All.Where(m => m.Role == "assistant").OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            .ToList();
}
