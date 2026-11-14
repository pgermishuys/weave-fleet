using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.Acp;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Progress;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Progress;

/// <summary>
/// A plan written and ticked straight away, as each harness reports the two calls: by the time Fleet applies the write,
/// the file on disk already holds the tick. Each call has to show what it left, not what the file holds now.
/// </summary>
public sealed partial class SessionProgressServiceTests
{
    private const string TickedStep = "2. Map the messages";

    [Fact]
    public async Task A_plan_Claude_Code_writes_and_ticks_back_to_back_shows_the_write_then_the_tick()
    {
        using var fixture = await CreatePlanFixtureAsync();

        var written = await ClaudeCodeFilesWrittenAsync(fixture,
            ToolCall("toolu_write", "Write", new JsonObject { ["file_path"] = fixture.PlanPath, ["content"] = PlanText }),
            ToolResult("toolu_write", "File created successfully",
                new JsonObject { ["type"] = "create", ["filePath"] = fixture.PlanPath, ["content"] = PlanText, ["structuredPatch"] = new JsonArray() }),
            ToolCall("toolu_tick", "Edit", new JsonObject
            {
                ["file_path"] = fixture.PlanPath, ["old_string"] = $"- [ ] {TickedStep}", ["new_string"] = $"- [x] {TickedStep}", ["replace_all"] = false,
            }),
            ToolResult("toolu_tick", "The file has been updated successfully.", new JsonObject
            {
                ["filePath"] = fixture.PlanPath, ["oldString"] = $"- [ ] {TickedStep}", ["newString"] = $"- [x] {TickedStep}",
                ["structuredPatch"] = new JsonArray(), ["userModified"] = false, ["replaceAll"] = false,
            }));

        await ShouldShowTheWriteThenTheTickAsync(fixture, written);
    }

    [Fact]
    public async Task A_plan_an_ACP_agent_writes_and_ticks_back_to_back_shows_the_write_then_the_tick()
    {
        using var fixture = await CreatePlanFixtureAsync();
        var mapper = new AcpMapper(fixture.SessionId);
        var events = new List<HarnessEvent>();

        // As weave-copilot reports Copilot's create and edit: the diff comes with the call, the result is only text.
        events.AddRange(mapper.Update(AcpCall("c1", fixture.PlanPath, new JsonObject { ["path"] = fixture.PlanPath, ["file_text"] = PlanText }, oldText: null, newText: PlanText)));
        events.AddRange(mapper.Update(AcpDone("c1", "Created file")));
        events.AddRange(mapper.Update(AcpCall("c2", fixture.PlanPath,
            new JsonObject { ["path"] = fixture.PlanPath, ["old_str"] = $"- [ ] {TickedStep}", ["new_str"] = $"- [x] {TickedStep}" },
            oldText: $"- [ ] {TickedStep}", newText: $"- [x] {TickedStep}")));
        events.AddRange(mapper.Update(AcpDone("c2", "File updated")));

        await ShouldShowTheWriteThenTheTickAsync(fixture, Written(events, fixture.SessionId));
    }

    /// <summary>
    /// Applies the write and the tick after both calls ran (the file already ticked): the plan shows step 1 of 3 done,
    /// then 2 of 3 with the tick's time and message.
    /// </summary>
    private static async Task ShouldShowTheWriteThenTheTickAsync(PlanFixture fixture, List<FilesWritten> written)
    {
        written.Count.ShouldBe(2);
        await File.WriteAllTextAsync(fixture.PlanPath, PlanText.Replace($"- [ ] {TickedStep}", $"- [x] {TickedStep}", StringComparison.Ordinal));
        var wroteAt = new DateTimeOffset(2026, 11, 14, 16, 0, 0, TimeSpan.Zero);

        var afterWrite = await fixture.Service.ApplyAsync(new ObservedProgressEvent(fixture.SessionId, OwnerId, written[0], wroteAt), CancellationToken.None);
        afterWrite.ShouldNotBeNull().Done.ShouldBe(1, "the write shows the plan it wrote, not the tick that came after it");

        var afterTick = await fixture.Service.ApplyAsync(new ObservedProgressEvent(fixture.SessionId, OwnerId, written[1], wroteAt.AddMilliseconds(15)), CancellationToken.None);
        afterTick.ShouldNotBeNull("the tick changes the plan").Done.ShouldBe(2);
        var step = afterTick.Plans.ShouldHaveSingleItem().Steps.Single(s => s.Key == "2");
        (step.TickedAt, step.TickedInMessageId).ShouldBe(((DateTimeOffset?)wroteAt.AddMilliseconds(15), written[1].Payload.MessageId));
    }

    private static List<FilesWritten> Written(IEnumerable<HarnessEvent> events, string sessionId)
    {
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);
        return events.Where(e => e.Type == EventTypes.FilesWritten)
            .Select(e => translator.Translate(e with { FleetSessionId = sessionId }))
            .OfType<FilesWritten>()
            .ToList();
    }

    /// <summary>The <c>files.written</c> events a real Claude Code session reports for <paramref name="lines"/> of its stream.</summary>
    private static async Task<List<FilesWritten>> ClaudeCodeFilesWrittenAsync(PlanFixture fixture, params string[] lines)
    {
        var messages = new InMemoryMessageRepository();
        var sessions = new InMemorySessionRepository();
        var connections = new FakeDbConnectionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, messages, new InMemoryDelegationRepository(), sessions, new InMemorySmartLinkRepository(), new InMemoryOutboxRepository(),
            new FakeOutboxDispatcher()));

        await using var session = new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fixture.SessionId,
            workingDirectory: fixture.Directory,
            config: new ClaudeCodeOptions { BinaryPath = "/nonexistent/claude" },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: OwnerId);

        var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")));
        await using (var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance))
            await session.PumpStdoutAsync(stdout, process);

        await session.StopAsync(CancellationToken.None);
        var events = new List<HarnessEvent>();
        await foreach (var evt in session.SubscribeAsync(CancellationToken.None))
            events.Add(evt);

        return Written(events, fixture.SessionId);
    }

    private static string ToolCall(string id, string name, JsonObject input) => new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["id"] = $"msg_{id}", ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }),
        },
        ["parent_tool_use_id"] = null,
        ["session_id"] = "claude-session",
    }.ToJsonString();

    private static string ToolResult(string id, string text, JsonObject result) => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["tool_use_id"] = id, ["type"] = "tool_result", ["content"] = text }),
        },
        ["parent_tool_use_id"] = null,
        ["session_id"] = "claude-session",
        ["tool_use_result"] = result,
    }.ToJsonString();

    private static JsonElement AcpCall(string id, string path, JsonObject rawInput, string? oldText, string newText) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["sessionUpdate"] = "tool_call", ["toolCallId"] = id, ["title"] = "Edit plan", ["kind"] = "edit", ["status"] = "pending",
        ["rawInput"] = rawInput,
        ["locations"] = new JsonArray(new JsonObject { ["path"] = path }),
        ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = path, ["oldText"] = oldText, ["newText"] = newText }),
    });

    private static JsonElement AcpDone(string id, string text) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } }),
    });
}
