using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// Reopening a session rebuilds it from OpenCode's history. Every part must come back exactly as the live stream
/// showed it; otherwise cards change or vanish when you navigate away and back.
/// </summary>
public sealed class OpenCodeReopenedSessionTests
{
    private const string FleetSessionId = "fleet-1";
    private const string HarnessSessionId = "ses_1";

    // Parts as OpenCode returns them from GET /session/{id}/message and sends them in message.part.updated.
    // Key names and casing checked against a real opencode.db (callID, state.error, state.title). The live
    // translator needs "type" and "status" first, as OpenCode sends them.
    private static readonly Dictionary<string, string> Parts = new()
    {
        ["text"] = """
            { "type": "text", "id": "prt_text", "sessionID": "ses_1", "messageID": "msg_1", "text": "Let me look." }
            """,
        ["reasoning"] = """
            { "type": "reasoning", "id": "prt_think", "sessionID": "ses_1", "messageID": "msg_1", "text": "Thinking.", "time": { "start": 1 } }
            """,
        ["sub-agent"] = """
            { "type": "tool", "id": "prt_task", "sessionID": "ses_1", "messageID": "msg_1", "callID": "toolu_task", "tool": "task",
              "state": { "status": "completed", "input": { "subagent_type": "thread", "description": "Map the code", "prompt": "Go" },
                         "output": "The sub-agent's answer", "title": "Map the code",
                         "metadata": { "sessionId": "ses_child", "parentSessionId": "ses_1", "truncated": false },
                         "time": { "start": 1, "end": 2 } } }
            """,
        ["visual"] = """
            { "type": "tool", "id": "prt_vis", "sessionID": "ses_1", "messageID": "msg_1", "callID": "toolu_vis", "tool": "visualize",
              "state": { "status": "completed", "input": { "type": "flow" }, "title": "Flow",
                         "output": "{\"$type\":\"visual/flow\",\"content\":{\"nodes\":[{\"id\":\"a\",\"label\":\"A\"}]}}",
                         "metadata": { "truncated": false }, "time": { "start": 1, "end": 2 } } }
            """,
        ["canvas"] = """
            { "type": "tool", "id": "prt_canvas", "sessionID": "ses_1", "messageID": "msg_1", "callID": "toolu_canvas", "tool": "fleet_canvas_open",
              "state": { "status": "completed", "input": { "kind": "diagram" }, "title": "Open canvas",
                         "output": "Opened \"Architecture\" (cv_1) at v1.",
                         "metadata": { "canvasId": "cv_1", "version": 1, "truncated": false }, "time": { "start": 1, "end": 2 } } }
            """,
        ["failed"] = """
            { "type": "tool", "id": "prt_fail", "sessionID": "ses_1", "messageID": "msg_1", "callID": "toolu_fail", "tool": "bash",
              "state": { "status": "error", "input": { "command": "exit 1" }, "error": "Command failed with exit code 1",
                         "time": { "start": 1, "end": 2 } } }
            """,
        ["running"] = """
            { "type": "tool", "id": "prt_run", "sessionID": "ses_1", "messageID": "msg_1", "callID": "toolu_run", "tool": "bash",
              "state": { "status": "running", "input": { "command": "sleep 5" }, "time": { "start": 1 } } }
            """,
    };

    public static TheoryData<string> PartNames => [.. Parts.Keys];

    [Theory]
    [MemberData(nameof(PartNames))]
    public async Task Reopened_session_shows_the_part_as_the_live_stream_did(string partName)
    {
        var live = TranslateLive(Parts[partName]);

        var snapshot = await ReopenAsync(Parts[partName]);
        var reopened = snapshot.Messages.ShouldHaveSingleItem().Parts.ShouldHaveSingleItem();

        Serialize(reopened).ShouldBe(Serialize(live));
    }

    [Fact]
    public async Task Reopened_session_links_the_sub_agent_card_to_its_delegation()
    {
        var delegations = new InMemoryDelegationRepository();
        delegations.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = FleetSessionId,
            ParentToolCallId = "toolu_task",
            ChildSessionId = "fleet-child",
            Title = "thread",
            Status = "completed",
            CreatedAt = "2026-09-13T00:00:00Z",
            UpdatedAt = "2026-09-13T00:00:00Z",
        });

        var snapshot = await ReopenAsync(Parts["sub-agent"], delegations);

        // The client shows the drill-in card when a delegation names the task call's id.
        var task = snapshot.Messages[0].Parts.OfType<ToolMessageEventPart>().ShouldHaveSingleItem();
        snapshot.Delegations.ShouldHaveSingleItem().ParentToolCallId.ShouldBe(task.CallId);
    }

    [Fact]
    public async Task Reopened_session_keeps_tool_results_and_metadata()
    {
        var snapshot = await ReopenAsync(Parts["visual"], Parts["canvas"], Parts["failed"]);
        var tools = snapshot.Messages[0].Parts.OfType<ToolMessageEventPart>().ToDictionary(t => t.CallId);

        var visual = tools["toolu_vis"].State.ShouldBeOfType<ToolCompletedState>();
        visual.Output.ShouldNotBeNull().GetString().ShouldStartWith("{\"$type\":\"visual/flow\"");

        var canvas = tools["toolu_canvas"].State.ShouldBeOfType<ToolCompletedState>();
        canvas.Metadata.ShouldNotBeNull().GetProperty("canvasId").GetString().ShouldBe("cv_1");
        canvas.Title.ShouldBe("Open canvas");

        tools["toolu_fail"].State.ShouldBeOfType<ToolErrorState>().Error.ShouldBe("Command failed with exit code 1");
    }

    private static MessageEventPart TranslateLive(string partJson)
    {
        var translator = new DomainEventTranslator(NullLogger<DomainEventTranslator>.Instance);
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            ["sessionID"] = JsonSerializer.SerializeToElement(HarnessSessionId),
            ["part"] = JsonDocument.Parse(partJson).RootElement.Clone(),
        });

        var translated = translator.Translate(new HarnessEvent
        {
            Type = EventTypes.MessagePartUpdated,
            SessionId = HarnessSessionId,
            FleetSessionId = FleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        });

        return translated.ShouldBeOfType<MessagePartUpdated>().Payload.Part;
    }

    private static Task<SessionSnapshot> ReopenAsync(params string[] partJsons)
        => ReopenAsync(partJsons, new InMemoryDelegationRepository());

    private static Task<SessionSnapshot> ReopenAsync(string partJson, InMemoryDelegationRepository delegations)
        => ReopenAsync([partJson], delegations);

    private static async Task<SessionSnapshot> ReopenAsync(string[] partJsons, InMemoryDelegationRepository delegations)
    {
        // The same path OpenCodeHarnessSession.GetMessagesAsync takes with the HTTP response.
        using var parts = JsonDocument.Parse($"[{string.Join(",", partJsons)}]");
        using var info = JsonDocument.Parse("""
            { "id": "msg_1", "sessionID": "ses_1", "role": "assistant", "time": { "created": 1000 }, "modelID": "m", "providerID": "p",
              "mode": "build", "agent": "build", "path": { "cwd": "/", "root": "/" }, "cost": 0,
              "tokens": { "input": 0, "output": 0, "reasoning": 0, "cache": { "read": 0, "write": 0 } } }
            """);
        var messages = OpenCodeMapper.ToHarnessMessages(
        [
            new OpenCodeMessageWithParts
            {
                Info = OpenCodeMessageDeserializer.DeserializeAssistantMessage(info.RootElement)!,
                Parts = OpenCodeHttpClient.DeserializeParts(parts.RootElement),
            },
        ]);

        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session
        {
            Id = FleetSessionId,
            InstanceId = "inst-1",
            HarnessType = "opencode",
            Title = "Session",
            Status = "active",
            UserId = "user-1",
        });
        var instances = new InstanceTracker();
        instances.Register("inst-1", new FakeHarnessSession("inst-1")
        {
            GetMessagesBehavior = (_, _) => Task.FromResult(new MessagePage(messages, false)),
        });

        var proxy = new OpenCodeSessionMessageProxy(
            sessions,
            instances,
            new SessionActivityTracker(),
            delegations,
            new FakeSessionSnapshotBuilder(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        return await proxy.GetSnapshotAsync(FleetSessionId);
    }

    private static string Serialize(MessageEventPart part)
        => JsonSerializer.Serialize(
            new MessagePartUpdatedPayload { SessionId = FleetSessionId, Part = part },
            InfrastructureJsonContext.Default.MessagePartUpdatedPayload);
}
