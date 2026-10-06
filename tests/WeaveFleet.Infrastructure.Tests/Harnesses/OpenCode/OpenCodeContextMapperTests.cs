using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>How full an OpenCode session's context is, read from its events in OpenCode's own shape.</summary>
public sealed class OpenCodeContextMapperTests
{
    private static OpenCodeSseEvent Event(string type, string propertiesJson)
        => new() { Type = type, Properties = JsonDocument.Parse(propertiesJson).RootElement };

    // An assistant message.updated as OpenCode 1.18 sends it once the call has finished.
    private static OpenCodeSseEvent Finished(string mode = "build", bool summary = false) => Event("message.updated", $$"""
        {
          "info": {
            "id": "msg_01", "sessionID": "ses_1", "role": "assistant", "summary": {{(summary ? "true" : "false")}},
            "time": { "created": 1759740000000, "completed": 1759740004000 },
            "modelID": "claude-sonnet-4-5", "providerID": "anthropic", "mode": "{{mode}}",
            "cost": 0.01,
            "tokens": { "total": 19308, "input": 6, "output": 106, "reasoning": 0, "cache": { "write": 7165, "read": 12031 } }
          }
        }
        """);

    [Fact]
    public void a_finished_calls_tokens_are_the_contexts_size()
    {
        var read = OpenCodeMapper.TryReadContextCall(Finished()).ShouldNotBeNull();

        read.MessageId.ShouldBe("msg_01");
        read.ProviderId.ShouldBe("anthropic");
        read.ModelId.ShouldBe("claude-sonnet-4-5");
        read.Call.ShouldBe(new ContextCall { Input = 6, CacheRead = 12031, CacheWrite = 7165, Output = 106, Reasoning = 0 });
        read.Call.Used.ShouldBe(19308);
    }

    [Fact]
    public void a_call_without_output_yet_is_skipped()
    {
        var streaming = Event("message.updated", """
            {
              "info": {
                "id": "msg_01", "sessionID": "ses_1", "role": "assistant", "time": { "created": 1759740000000 },
                "tokens": { "input": 0, "output": 0, "reasoning": 0, "cache": { "write": 0, "read": 0 } }
              }
            }
            """);

        OpenCodeMapper.TryReadContextCall(streaming).ShouldBeNull();
    }

    [Fact]
    public void the_message_a_compaction_writes_is_skipped()
    {
        // Its tokens are the summarising call's, not the new context's.
        OpenCodeMapper.TryReadContextCall(Finished(summary: true)).ShouldBeNull();
        OpenCodeMapper.TryReadContextCall(Finished(mode: "compaction")).ShouldBeNull();
    }

    [Fact]
    public void user_messages_and_other_events_are_skipped()
    {
        OpenCodeMapper.TryReadContextCall(Event("message.updated", """{ "info": { "id": "msg_00", "sessionID": "ses_1", "role": "user", "time": { "created": 1 } } }""")).ShouldBeNull();
        OpenCodeMapper.TryReadContextCall(Event("session.idle", """{ "sessionID": "ses_1" }""")).ShouldBeNull();
    }

    [Theory]
    [InlineData(true, ContextCompactionTriggers.Auto)]
    [InlineData(false, ContextCompactionTriggers.Manual)]
    public void a_compaction_part_starts_a_compaction(bool auto, string trigger)
    {
        var evt = Event("message.part.updated", $$"""
            { "part": { "id": "prt_1", "sessionID": "ses_1", "messageID": "msg_02", "type": "compaction", "auto": {{(auto ? "true" : "false")}} } }
            """);

        var mapped = OpenCodeMapper.TryMapCompactionStarted(evt, "fleet-1").ShouldNotBeNull();

        mapped.Type.ShouldBe(EventTypes.ContextCompaction);
        var report = ContextEvents.ReadCompaction(mapped).ShouldNotBeNull();
        report.Phase.ShouldBe(ContextCompactionPhases.Started);
        report.Trigger.ShouldBe(trigger);
    }

    [Fact]
    public void other_parts_start_nothing()
        => OpenCodeMapper.TryMapCompactionStarted(Event("message.part.updated", """{ "part": { "type": "text", "text": "hi" } }"""), "fleet-1").ShouldBeNull();

    [Theory]
    // No input limit: the window less the output OpenCode allows (capped at 32,000).
    [InlineData(200_000, null, 64_000, 168_000)]
    [InlineData(128_000, null, 16_384, 111_616)]
    // An input limit: that, less the output room (at most 20,000).
    [InlineData(200_000, 136_000, 64_000, 116_000)]
    // No output limit: OpenCode takes 32,000.
    [InlineData(200_000, null, 0, 168_000)]
    public void compacts_where_opencode_does(int context, int? input, int output, int expected)
        => OpenCodeMapper.CompactsAt(new OpenCodeModelLimit { Context = context, Input = input, Output = output }).ShouldBe(expected);

    [Fact]
    public void an_unknown_window_says_nothing_about_compacting()
        => OpenCodeMapper.CompactsAt(new OpenCodeModelLimit()).ShouldBeNull();

    [Fact]
    public void the_provider_list_carries_each_models_limits()
    {
        var response = JsonSerializer.Deserialize("""
            { "all": [ { "id": "anthropic", "models": { "claude-haiku-4.5": { "id": "claude-haiku-4.5", "limit": { "context": 200000, "input": 136000, "output": 64000 } } } } ], "default": {} }
            """, WeaveFleet.Infrastructure.OpenCodeJsonContext.Default.OpenCodeProvidersResponse).ShouldNotBeNull();

        var limit = response.All[0].Models["claude-haiku-4.5"].Limit.ShouldNotBeNull();
        limit.Context.ShouldBe(200_000);
        limit.Input.ShouldBe(136_000);
        limit.Output.ShouldBe(64_000);
    }
}
