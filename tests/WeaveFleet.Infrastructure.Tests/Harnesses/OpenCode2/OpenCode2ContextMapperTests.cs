using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>How full an OpenCode 2 session's context is, read from V2's events.</summary>
public sealed class OpenCode2ContextMapperTests
{
    private const string FleetSession = "fleet-session-1";

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_1",
        Created = 1_789_763_753_000,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };

    [Fact]
    public async Task each_recorded_step_is_a_call_with_its_model()
    {
        var mapper = new OpenCode2Mapper(FleetSession);
        var calls = new List<(ContextCall Call, string? ProviderId, string? ModelId)>();
        foreach (var evt in (await OpenCode2Fixtures.ReadEventsAsync("text-and-tool-turn.sse")).Where(e => e.SessionId == OpenCode2Fixtures.TextSession))
        {
            if (mapper.TryReadContextCall(evt) is { } call)
                calls.Add(call);
            mapper.Map(evt);
        }

        calls.Count.ShouldBe(3);
        calls.ShouldAllBe(c => c.ModelId == "fake-model" && c.ProviderId == "fakellm");
        calls[0].Call.Used.ShouldBe(15);
    }

    // Seen live on OpenCode 2.0.18: each kind is counted once, so the context is all five added up.
    [Fact]
    public void a_steps_tokens_are_the_contexts_size()
    {
        var read = new OpenCode2Mapper(FleetSession).TryReadContextCall(
            Event("session.step.ended", """{"sessionID":"ses_1","tokens":{"input":2,"output":776,"reasoning":29,"cache":{"read":47185,"write":611}},"cost":0.02}"""))
            .ShouldNotBeNull();

        read.Call.ShouldBe(new ContextCall { Input = 2, CacheRead = 47185, CacheWrite = 611, Output = 776, Reasoning = 29 });
        read.Call.Used.ShouldBe(48603);
    }

    [Theory]
    [InlineData("session.step.failed", """{"sessionID":"ses_1","tokens":{"input":2,"output":1,"reasoning":0,"cache":{"read":0,"write":0}}}""")]
    [InlineData("session.usage.updated", """{"sessionID":"ses_1","tokens":{"input":200,"output":100,"reasoning":0,"cache":{"read":0,"write":0}}}""")]
    [InlineData("session.step.ended", """{"sessionID":"ses_1","tokens":{"input":0,"output":0,"reasoning":0,"cache":{"read":0,"write":0}}}""")]
    public void other_events_and_empty_steps_are_not_a_call(string type, string data)
        => new OpenCode2Mapper(FleetSession).TryReadContextCall(Event(type, data)).ShouldBeNull();

    [Theory]
    [InlineData("""{"sessionID":"ses_1","reason":"auto","recent":2}""", ContextCompactionTriggers.Auto)]
    [InlineData("""{"sessionID":"ses_1","reason":"manual"}""", ContextCompactionTriggers.Manual)]
    public void a_compaction_starting_says_what_started_it(string data, string trigger)
    {
        var mapped = new OpenCode2Mapper(FleetSession).TryMapCompaction(Event("session.compaction.started", data)).ShouldNotBeNull();

        mapped.FleetSessionId.ShouldBe(FleetSession);
        var report = ContextEvents.ReadCompaction(mapped).ShouldNotBeNull();
        report.Phase.ShouldBe(ContextCompactionPhases.Started);
        report.Trigger.ShouldBe(trigger);
    }

    [Fact]
    public void a_compaction_ends_or_fails_with_its_reason()
    {
        var mapper = new OpenCode2Mapper(FleetSession);

        ContextEvents.ReadCompaction(mapper.TryMapCompaction(Event("session.compaction.ended", """{"sessionID":"ses_1","text":"summary"}""")).ShouldNotBeNull())!
            .Phase.ShouldBe(ContextCompactionPhases.Ended);
        var failed = ContextEvents.ReadCompaction(mapper.TryMapCompaction(
            Event("session.compaction.failed", """{"sessionID":"ses_1","error":{"name":"CompactionUnavailable","message":"Nothing to compact yet"}}""")).ShouldNotBeNull())!;
        failed.Phase.ShouldBe(ContextCompactionPhases.Failed);
        failed.Error.ShouldBe("Nothing to compact yet");
        mapper.TryMapCompaction(Event("session.compaction.delta", """{"sessionID":"ses_1","text":"su"}""")).ShouldBeNull();
    }

    [Fact]
    public void the_model_list_carries_each_models_limits()
    {
        var models = JsonSerializer.Deserialize(
            """{"location":"/work","data":[{"id":"claude-opus-5","providerID":"anthropic","name":"Opus","limit":{"context":1000000,"output":64000}}]}""",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2ModelInfo).ShouldNotBeNull();

        models.Data.ShouldNotBeNull()[0].Limit.ShouldNotBeNull().Context.ShouldBe(1_000_000);
    }
}
