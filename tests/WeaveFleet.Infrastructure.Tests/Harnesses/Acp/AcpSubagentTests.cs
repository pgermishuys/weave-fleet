using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

public sealed class AcpSubagentTests
{
    private static AcpHarnessSession NewSession(FakeAcpAgent agent, string? childId = "child-1") =>
        new("copilot", "copilot-1", "fleet-1", "acp-1", "/work", agent, NullLogger.Instance)
        {
            ChildSessions = (_, _, _) => Task.FromResult(childId),
        };

    private static JsonObject Chunk(string text, string? call = null, string kind = "agent_message_chunk")
    {
        var update = new JsonObject { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } };
        if (call is not null)
            update["_meta"] = new JsonObject { ["parentToolCallId"] = call };
        return update;
    }

    private static void Work(FakeAcpAgent agent, string ev, string workId = "call-1", string kind = "subagent", bool background = false, string? reason = null)
        => agent.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "_fleet/work",
            ["params"] = new JsonObject
            {
                ["sessionId"] = "acp-1", ["event"] = ev, ["workId"] = workId, ["kind"] = kind, ["title"] = "explore", ["label"] = "Find the bug",
                ["toolCallId"] = workId, ["background"] = background, ["canStop"] = true, ["canReadOutput"] = kind == "shell", ["endedReason"] = reason,
            },
        });

    private static async Task<List<HarnessEvent>> ReadAsync(AcpHarnessSession session, Func<List<HarnessEvent>, bool> until)
    {
        var seen = new List<HarnessEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in session.SubscribeAsync(timeout.Token))
        {
            seen.Add(evt);
            if (until(seen))
                break;
        }

        return seen;
    }

    private static string? StatusOf(HarnessEvent e)
        => e.Type == EventTypes.SessionStatus ? e.Payload!.Value.GetProperty("status").GetProperty("type").GetString() : null;

    private static bool ChildIdle(List<HarnessEvent> seen) => seen.Any(e => e.FleetSessionId == "child-1" && StatusOf(e) == "idle");

    [Fact]
    public async Task a_subagents_updates_go_to_its_own_conversation_and_never_to_the_parents()
    {
        await using var agent = new FakeAcpAgent();
        await using var session = NewSession(agent);

        // Said before its session is known: kept, and given to it when it is.
        agent.Update("acp-1", Chunk("Looking", "call-1"));
        Work(agent, "started");
        agent.Update("acp-1", Chunk(" around", "call-1"));
        Work(agent, "ended", reason: "completed");
        var events = await ReadAsync(session, ChildIdle);

        var child = events.Where(e => e.FleetSessionId == "child-1").ToList();
        child.ShouldAllBe(e => e.SessionId == "child-1");
        StatusOf(child[0]).ShouldBe("busy");
        child.Select(e => e.Type).ShouldContain(EventTypes.MessagePartUpdated);
        events.Where(e => e.FleetSessionId is null).Select(e => e.Type).ShouldBe([EventTypes.WorkStarted, EventTypes.WorkEnded]);
        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.ShouldBeEmpty();
        session.ChildMessages("call-1").Single().Role.ShouldBe("assistant");
    }

    [Fact]
    public async Task work_started_and_ended_carry_the_report_and_the_session_tracks_what_runs()
    {
        await using var agent = new FakeAcpAgent();
        await using var session = NewSession(agent);

        Work(agent, "started");
        Work(agent, "started", "shell-1", "shell", background: true);
        var started = await ReadAsync(session, s => s.Count(e => e.Type == EventTypes.WorkStarted) == 2);

        var reports = started.Where(e => e.Type == EventTypes.WorkStarted).Select(e => WorkEvents.Read(e)!).ToList();
        reports[0].ShouldBe(new WorkReport
        {
            WorkId = "call-1", Kind = "subagent", Title = "explore", Label = "Find the bug", ToolCallId = "call-1", Background = false,
            ChildHarnessSessionId = "call-1", CanStop = true, CanReadOutput = false,
        });
        (reports[1].Kind, reports[1].ChildHarnessSessionId, reports[1].CanReadOutput, reports[1].Background).ShouldBe(("shell", null, true, true));
        (await session.GetRunningWorkAsync(CancellationToken.None))!.Select(r => r.WorkId).ShouldBe(["call-1", "shell-1"]);

        Work(agent, "ended", "shell-1", "shell", reason: "error");
        var ended = await ReadAsync(session, s => s.Any(e => e.Type == EventTypes.WorkEnded));
        var end = WorkEvents.Read(ended.Single(e => e.Type == EventTypes.WorkEnded))!;
        (end.WorkId, end.EndedReason, end.Title).ShouldBe(("shell-1", "error", "explore"));
        (await session.GetRunningWorkAsync(CancellationToken.None))!.Select(r => r.WorkId).ShouldBe(["call-1"]);
    }

    [Fact]
    public async Task a_cancelled_subagent_settles_its_calls_and_goes_idle()
    {
        await using var agent = new FakeAcpAgent();
        await using var session = NewSession(agent);
        agent.Update("acp-1", new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Read", ["kind"] = "read", ["status"] = "in_progress",
            ["_meta"] = new JsonObject { ["parentToolCallId"] = "call-1" },
        });

        Work(agent, "started");
        Work(agent, "ended", reason: "cancelled");
        await ReadAsync(session, ChildIdle);

        var tool = session.ChildMessages("call-1").SelectMany(m => m.Parts).OfType<ToolUsePart>().Single();
        tool.State.ShouldBe(ToolUseState.Error);
        tool.Error.ShouldBe("Stopped before it finished.");
    }

    [Fact]
    public async Task the_parents_turn_ending_settles_a_subagent_that_is_still_busy_but_not_a_background_one()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                await release.Task;
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.SendPromptAsync("go", null, CancellationToken.None);
        Work(agent, "started", "sync-1");
        Work(agent, "started", "bg-1", background: true);
        await ReadAsync(session, s => s.Count(e => e.FleetSessionId == "child-1" && StatusOf(e) == "busy") == 2);

        release.SetResult();
        var events = await ReadAsync(session, s => s.Any(e => e.FleetSessionId == "child-1" && StatusOf(e) == "idle"));

        events.Count(e => e.FleetSessionId == "child-1" && StatusOf(e) == "idle").ShouldBe(1);
    }

    [Fact]
    public async Task a_replay_builds_the_child_conversation_and_emits_nothing()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, _) =>
        {
            if (method == "session/load")
            {
                agent.Update("acp-1", Chunk("Fix it", kind: "user_message_chunk"));
                agent.Update("acp-1", Chunk("Look for the bug", "call-1", "user_message_chunk"));
                agent.Update("acp-1", Chunk("Found it", "call-1"));
                agent.Update("acp-1", Chunk("Done."));
            }

            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        await using var session = NewSession(agent);

        await session.LoadAsync(null, CancellationToken.None);
        await using var child = new AcpChildSession("copilot-2", "call-1", () => Task.FromResult<AcpHarnessSession?>(session));

        var parent = (await session.GetMessagesAsync(null, CancellationToken.None)).Messages;
        parent.Select(m => m.Role).ShouldBe(["user", "assistant"]);
        var served = (await child.GetMessagesAsync(null, CancellationToken.None)).Messages;
        served.Select(m => m.Role).ShouldBe(["user", "assistant"]);
        served.SelectMany(m => m.Parts).OfType<TextPart>().Select(p => p.Text).ShouldBe(["Look for the bug", "Found it"]);
        (await child.GetMessagesAsync(new MessageQuery(Limit: 1), CancellationToken.None)).ShouldSatisfyAllConditions(
            p => p.Messages.Count.ShouldBe(1), p => p.HasMore.ShouldBeTrue());
        using var none = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.SubscribeAsync(none.Token))
            {
            }
        });
    }

    [Fact]
    public async Task stopping_and_reading_work_send_the_requests_and_map_the_answers()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, p) => Task.FromResult<JsonNode?>(method switch
        {
            "_fleet/work/stop" => new JsonObject { ["stopped"] = p.GetProperty("workId").GetString() == "shell-1" },
            "_fleet/work/output" when p.GetProperty("workId").GetString() == "shell-1"
                => new JsonObject { ["text"] = "hello\n", ["nextOffset"] = 16, ["size"] = 16, ["truncated"] = true },
            _ => null,
        });
        await using var session = NewSession(agent);

        (await session.StopWorkAsync("shell-1", CancellationToken.None)).ShouldBeTrue();
        (await session.StopWorkAsync("gone", CancellationToken.None)).ShouldBeFalse();
        (await session.ReadWorkOutputAsync("shell-1", 10, CancellationToken.None)).ShouldBe(new WorkOutput("hello\n", 16, 16, true));
        (await session.ReadWorkOutputAsync("call-1", 0, CancellationToken.None)).ShouldBeNull();

        var asked = agent.Received.Where(r => r.Method == "_fleet/work/output").First().Params;
        (asked.GetProperty("sessionId").GetString(), asked.GetProperty("workId").GetString(), asked.GetProperty("offset").GetInt64()).ShouldBe(("acp-1", "shell-1", 10L));
    }

    [Fact]
    public async Task a_relaunch_ends_the_work_the_old_process_ran_as_lost()
    {
        await using var first = new FakeAcpAgent();
        await using var second = new FakeAcpAgent();
        second.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        var off = new FleetToolSwitches(SessionMessages: false, Memory: false, WorkflowStep: false, Browser: false);
        await using var session = new AcpHarnessSession("copilot", "copilot-1", "fleet-1", "acp-1", "/work", first, NullLogger.Instance)
        {
            ChildSessions = (_, _, _) => Task.FromResult<string?>("child-1"),
            FleetTools = off,
            ReadFleetTools = () => Task.FromResult<FleetToolSwitches?>(off with { Browser = true }),
            Relaunch = _ => Task.FromResult<IAcpAgent>(second),
        };
        Work(first, "started", "shell-1", "shell");
        await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.WorkStarted));

        await session.SendPromptAsync("next", null, CancellationToken.None);
        var seen = await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.WorkEnded));

        WorkEvents.Read(seen.Single(e => e.Type == EventTypes.WorkEnded))!.EndedReason.ShouldBe(WorkEndedReasons.Lost);
        (await session.GetRunningWorkAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_older_weave_copilot_that_doesnt_know_the_work_requests_is_not_supported()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => throw new AcpException("Method not found", -32601);
        await using var session = NewSession(agent);

        await Should.ThrowAsync<NotSupportedException>(() => session.StopWorkAsync("shell-1", CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(() => session.ReadWorkOutputAsync("shell-1", 0, CancellationToken.None));
    }
}
