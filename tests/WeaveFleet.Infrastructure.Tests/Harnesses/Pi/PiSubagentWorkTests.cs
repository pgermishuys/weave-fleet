using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

/// <summary>
/// Pi's example subagent extension as running work. The fixtures are real Pi 0.84.2 RPC transcripts (<c>pi --mode rpc</c>
/// as Fleet starts it) with the extension in <c>~/.pi/agent/extensions/subagent</c>, the scout and reviewer agents on
/// <c>fakellm/fake-b</c>, and a scripted model calling the <c>subagent</c> tool in each mode.
/// </summary>
public sealed class PiSubagentWorkTests
{
    [Fact]
    public void One_agent_is_running_work_until_its_last_turn_stops_not_while_it_calls_tools()
    {
        var (events, callId) = Replay("subagent-single.jsonl");

        Work(events).ShouldBe(
        [
            (EventTypes.WorkStarted, $"{callId}:0", "scout", "Find the note with a tool", "fake-b", null),
            (EventTypes.WorkEnded, $"{callId}:0", "scout", "Find the note with a tool", "fake-b · Done by fake-b.", WorkEndedReasons.Completed),
        ]);

        // It ended with its own last turn, before the call did.
        var ended = events.FindIndex(evt => evt.Type == EventTypes.WorkEnded);
        ended.ShouldBeLessThan(events.FindIndex(evt => IsToolStatus(evt, "completed")));

        var started = Report(events.First(evt => evt.Type == EventTypes.WorkStarted));
        started.Kind.ShouldBe(WorkKinds.Subagent);
        started.ToolCallId.ShouldBe(callId);
        started.ChildHarnessSessionId.ShouldBeNull();
        started.CanStop.ShouldBe(false);
        started.CanReadOutput.ShouldBe(false);
        started.Background.ShouldBeNull();
    }

    [Fact]
    public void Agents_side_by_side_are_each_their_own_work_and_end_as_each_finishes()
    {
        var (events, callId) = Replay("subagent-parallel.jsonl");

        // The scout hadn't started when the first update came (no model yet); the reviewer finished first.
        Work(events).ShouldBe(
        [
            (EventTypes.WorkStarted, $"{callId}:0", "scout", "Find the models", null, null),
            (EventTypes.WorkStarted, $"{callId}:1", "reviewer", "Review the diff", "fake-b", null),
            (EventTypes.WorkEnded, $"{callId}:1", "reviewer", "Review the diff", "fake-b · Done by fake-b.", WorkEndedReasons.Completed),
            (EventTypes.WorkUpdated, $"{callId}:0", "scout", "Find the models", "fake-b", null),
            (EventTypes.WorkEnded, $"{callId}:0", "scout", "Find the models", "fake-b · Done by fake-b.", WorkEndedReasons.Completed),
        ]);
    }

    [Fact]
    public void A_chain_runs_one_step_at_a_time_and_says_which_step()
    {
        var (events, callId) = Replay("subagent-chain.jsonl");

        Work(events).ShouldBe(
        [
            (EventTypes.WorkStarted, $"{callId}:0", "scout", "Find the note", "step 1 of 2 · fake-b", null),
            (EventTypes.WorkEnded, $"{callId}:0", "scout", "Find the note", "step 1 of 2 · fake-b · Done by fake-b.", WorkEndedReasons.Completed),
            (EventTypes.WorkStarted, $"{callId}:1", "reviewer", "Review what came back: Done by fake-b.", "step 2 of 2 · fake-b", null),
            (EventTypes.WorkEnded, $"{callId}:1", "reviewer", "Review what came back: Done by fake-b.", "step 2 of 2 · fake-b · Done by fake-b.", WorkEndedReasons.Completed),
        ]);
    }

    [Fact]
    public void An_agent_that_fails_to_start_ends_in_error_with_its_reason()
    {
        // Pi reports the call as a success; the agent's exit code and stderr say otherwise.
        var (events, callId) = Replay("subagent-unknown-agent.jsonl");

        Work(events).ShouldBe(
        [
            (EventTypes.WorkStarted, $"{callId}:0", "nobody", "Do anything", null, null),
            (EventTypes.WorkEnded, $"{callId}:0", "nobody", "Do anything", "exit 1: Unknown agent: \"nobody\". Available agents: \"reviewer\", \"scout\".", WorkEndedReasons.Error),
        ]);
    }

    [Fact]
    public void Interrupting_the_turn_cancels_the_agents_it_was_running()
    {
        // The interrupted call reports no results, only "Subagent was aborted".
        var (events, callId) = Replay("subagent-abort.jsonl");

        Work(events).ShouldBe(
        [
            (EventTypes.WorkStarted, $"{callId}:0", "scout", "Do slow work", "fake-b", null),
            (EventTypes.WorkEnded, $"{callId}:0", "scout", "Do slow work", "fake-b", WorkEndedReasons.Cancelled),
        ]);
    }

    [Fact]
    public void Running_work_is_what_the_calls_running_now_have_started()
    {
        var mapper = new PiMapper("transcript-session");
        var counts = new List<int>();
        foreach (var evt in Stdout("subagent-parallel.jsonl"))
        {
            mapper.Map(evt);
            if (evt is PiToolExecutionUpdateEvent or PiToolExecutionEndEvent)
                counts.Add(mapper.RunningWork().Count);
        }

        // Both run, the reviewer finishes, then the scout.
        counts.ShouldBe([2, 1, 1, 1, 0, 0, 0]);
    }

    [Fact]
    public void A_call_whose_end_never_came_ends_with_pis_run()
    {
        var mapper = new PiMapper("transcript-session");
        var events = Stdout("subagent-single.jsonl")
            .TakeWhile(evt => evt is not PiToolExecutionUpdateEvent { PartialResult.Content: [PiTextContent { Text: "Done by fake-b." }] })
            .SelectMany(mapper.Map)
            .ToList();
        mapper.RunningWork().ShouldHaveSingleItem();

        var ended = mapper.Map(new PiAgentEndEvent());

        mapper.RunningWork().ShouldBeEmpty();
        var end = ended.Where(evt => evt.Type == EventTypes.WorkEnded).ShouldHaveSingleItem();
        Report(end).EndedReason.ShouldBe(WorkEndedReasons.Cancelled);
        events.ShouldNotContain(evt => evt.Type == EventTypes.WorkEnded);
    }

    [Fact]
    public void Other_tools_are_not_running_work()
    {
        var mapper = new PiMapper("transcript-session");

        Stdout("tool-validation.jsonl").SelectMany(mapper.Map).ShouldNotContain(evt => EventTypes.IsWorkEvent(evt.Type));
    }

    [Fact]
    public void A_subagent_tool_of_another_shape_is_only_a_tool_card()
    {
        var mapper = new PiMapper("transcript-session");
        using var details = JsonDocument.Parse("""{"results":[{"name":"x"}]}""");

        var events = mapper.Map(new PiToolExecutionUpdateEvent
        {
            ToolCallId = "call_other",
            ToolName = PiSubagentWork.ToolName,
            PartialResult = new PiToolResult { Details = details.RootElement.Clone() },
        });

        events.ShouldNotContain(evt => EventTypes.IsWorkEvent(evt.Type));
        mapper.RunningWork().ShouldBeEmpty();
    }

    private static List<(string Type, string WorkId, string? Title, string? Label, string? Detail, string? Reason)> Work(List<HarnessEvent> events)
        => events
            .Where(evt => EventTypes.IsWorkEvent(evt.Type))
            .Select(evt => (evt.Type, Report(evt)))
            .Select(e => (e.Type, e.Item2.WorkId, e.Item2.Title, e.Item2.Label, e.Item2.Detail, e.Item2.EndedReason))
            .ToList();

    private static WorkReport Report(HarnessEvent evt) => WorkEvents.Read(evt).ShouldNotBeNull();

    private static bool IsToolStatus(HarnessEvent evt, string status)
        => evt.Type == EventTypes.MessagePartUpdated
            && evt.Payload is { } payload
            && payload.TryGetProperty("part", out var part)
            && part.TryGetProperty("state", out var state)
            && state.TryGetProperty("status", out var s)
            && s.GetString() == status;

    private static (List<HarnessEvent> Events, string CallId) Replay(string fixture)
    {
        var mapper = new PiMapper("transcript-session");
        var stdout = Stdout(fixture);
        var callId = stdout.OfType<PiToolExecutionStartEvent>().Single(evt => evt.ToolName == PiSubagentWork.ToolName).ToolCallId;
        var events = stdout.SelectMany(mapper.Map).ToList();
        mapper.RunningWork().ShouldBeEmpty();
        return (events, callId);
    }

    /// <summary>The fixture's Pi events, in the order Pi wrote them.</summary>
    private static List<PiEvent> Stdout(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pi", fixture);
        var events = new List<PiEvent>();
        foreach (var line in File.ReadLines(path))
        {
            using var wrapper = JsonDocument.Parse(line);
            if (wrapper.RootElement.GetProperty("stream").GetString() != "stdout")
                continue;

            var json = PiJsonlClient.NormalizeDiscriminatorsFirst(wrapper.RootElement.GetProperty("line").GetString()!);
            var evt = JsonSerializer.Deserialize(json, PiJsonContext.Default.PiEvent);
            events.Add(evt.ShouldNotBeNull());
        }

        return events;
    }
}
