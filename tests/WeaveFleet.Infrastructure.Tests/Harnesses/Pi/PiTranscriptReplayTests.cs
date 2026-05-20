using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

public sealed class PiTranscriptReplayTests
{
    [Fact]
    public void prompt_text_streaming_fixture_replays_openrouter_events()
    {
        var events = ReplayFixture("resume-validation.jsonl");

        events.Count.ShouldBe(69);
        AssertTypeCounts(events, new Dictionary<string, int>
        {
            [EventTypes.SessionUpdated] = 4,
            [EventTypes.SessionStatus] = 8,
            [EventTypes.MessageCreated] = 4,
            [EventTypes.MessageUpdated] = 10,
            [EventTypes.MessagePartUpdated] = 41,
            [EventTypes.MessagePartDelta] = 2,
        });

        AssertInOrder(
            events,
            ("initial state", evt => evt.Type == EventTypes.SessionUpdated && ContainsProvider(evt, "openrouter")),
            ("agent busy", evt => IsStatus(evt, "busy")),
            ("turn busy", evt => IsStatus(evt, "busy")),
            ("user created", evt => evt.Type == EventTypes.MessageCreated && MessageRole(evt) == "user"),
            ("assistant created", evt => evt.Type == EventTypes.MessageCreated && MessageRole(evt) == "assistant"),
            ("reasoning starts", evt => IsPart(evt, "reasoning") && PartText(evt).StartsWith("The user wants", StringComparison.Ordinal)),
            ("text delta OK", evt => IsTextDelta(evt, "OK")),
            ("text final OK", evt => IsPart(evt, "text") && PartText(evt) == "OK"),
            ("assistant completed", evt => evt.Type == EventTypes.MessageUpdated && MessageRole(evt) == "assistant" && LifecycleCompleted(evt).HasValue),
            ("step finish", evt => IsPart(evt, "step-finish") && PartReason(evt) == "stop"),
            ("turn idle", evt => IsStatus(evt, "idle") && StatusReason(evt) == "stop"),
            ("post-turn state", evt => evt.Type == EventTypes.SessionUpdated && Payload(evt).GetProperty("messageCount").GetInt32() == 2));
    }

    [Fact]
    public void resume_fixture_replays_switch_session_with_session_file()
    {
        var transcript = LoadFixture("resume-validation.jsonl");
        var events = ReplayFixture(transcript);

        transcript.ShouldContain(e =>
            e.Stream == "stdin"
            && EventType(e) == "switch_session"
            && e.Payload.GetProperty("sessionPath").GetString()!.Contains("<PI_SESSION_DIR>", StringComparison.Ordinal));

        var sessionUpdates = events.Where(evt => evt.Type == EventTypes.SessionUpdated).ToList();
        sessionUpdates.Count.ShouldBe(4);
        sessionUpdates.ShouldContain(evt => Payload(evt).GetProperty("sessionFile").GetString()!.Contains("<PI_SESSION_DIR>", StringComparison.Ordinal));

        AssertInOrder(
            events,
            ("first prompt final text", evt => IsPart(evt, "text") && PartText(evt) == "OK"),
            ("first prompt idle", evt => IsStatus(evt, "idle") && StatusReason(evt) == "stop"),
            ("resumed prompt busy", evt => IsStatus(evt, "busy")),
            ("resumed prompt final text", evt => IsPart(evt, "text") && PartText(evt) == "pineapple"),
            ("resumed prompt idle", evt => IsStatus(evt, "idle") && StatusReason(evt) == "stop"),
            ("final resumed state", evt => evt.Type == EventTypes.SessionUpdated && Payload(evt).GetProperty("messageCount").GetInt32() == 4));
    }

    [Fact]
    public void tool_fixture_replays_toolcall_and_tool_execution_events()
    {
        var events = ReplayFixture("tool-validation.jsonl");

        events.Count.ShouldBe(96);
        AssertTypeCounts(events, new Dictionary<string, int>
        {
            [EventTypes.SessionUpdated] = 2,
            [EventTypes.SessionStatus] = 6,
            [EventTypes.MessageCreated] = 4,
            [EventTypes.MessageUpdated] = 10,
            [EventTypes.MessagePartUpdated] = 49,
            [EventTypes.MessagePartDelta] = 25,
        });

        AssertInOrder(
            events,
            ("assistant created", evt => evt.Type == EventTypes.MessageCreated && MessageRole(evt) == "assistant"),
            ("tool pending start", evt => IsTool(evt, "pending") && ToolCallId(evt).EndsWith("-content-1", StringComparison.Ordinal)),
            ("tool pending named", evt => IsTool(evt, "pending") && ToolName(evt) == "bash" && ToolCallId(evt) == "toolu_bdrk_01FyZaRDQHHxCpfMaZmr5Pwc"),
            ("tool running", evt => IsTool(evt, "running") && ToolName(evt) == "bash" && HasObjectToolInput(evt) && ToolInput(evt).GetProperty("command").GetString() == "ls -la"),
            ("tool completed", evt => IsTool(evt, "completed") && ToolOutput(evt).GetString()!.Contains("resume-validation.jsonl", StringComparison.Ordinal)),
            ("tool result created", evt => evt.Type == EventTypes.MessageCreated && MessageAgent(evt) == "tool:bash"),
            ("first turn idle", evt => IsStatus(evt, "idle") && StatusReason(evt) == "toolUse"),
            ("summary text delta", evt => evt.Type == EventTypes.MessagePartDelta && Delta(evt).Contains("Summary", StringComparison.Ordinal)),
            ("summary final text", evt => IsPart(evt, "text") && PartText(evt).Contains("resume-validation.jsonl", StringComparison.Ordinal)),
            ("second turn idle", evt => IsStatus(evt, "idle") && StatusReason(evt) == "stop"));
    }

    [Fact]
    public void abort_error_fixture_replays_abort_idle_and_unknown_command()
    {
        var transcript = LoadFixture("abort-error-validation.jsonl");
        var events = ReplayFixture(transcript);

        transcript.ShouldContain(e => e.Stream == "stdin" && EventType(e) == "abort");
        transcript.ShouldContain(e => IsPiResponse(e, "abort", true));

        events.Count.ShouldBe(17);
        AssertTypeCounts(events, new Dictionary<string, int>
        {
            [EventTypes.SessionUpdated] = 2,
            [EventTypes.SessionStatus] = 4,
            [EventTypes.MessageCreated] = 2,
            [EventTypes.MessageUpdated] = 5,
            [EventTypes.MessagePartUpdated] = 2,
            [EventTypes.SessionError] = 1,
            [EventTypes.Error] = 1,
        });

        AssertInOrder(
            events,
            ("agent busy", evt => IsStatus(evt, "busy")),
            ("assistant aborted", evt => evt.Type == EventTypes.MessageUpdated && MessageRole(evt) == "assistant" && LifecycleCompleted(evt).HasValue),
            ("aborted step", evt => IsPart(evt, "step-finish") && PartReason(evt) == "aborted"),
            ("abort session error", evt => evt.Type == EventTypes.SessionError && ErrorMessage(evt) == "Request was aborted."),
            ("idle after abort", evt => IsStatus(evt, "idle") && StatusReason(evt) == "aborted"),
            ("abort state idle", evt => evt.Type == EventTypes.SessionUpdated && Payload(evt).GetProperty("isStreaming").GetBoolean() == false),
            ("unknown command protocol error", evt => evt.Type == EventTypes.Error && ErrorMessage(evt) == "Unknown command: not_a_real_command"),
            ("error state idle", evt => evt.Type == EventTypes.SessionUpdated && Payload(evt).GetProperty("pendingMessageCount").GetInt32() == 0));
    }

    [Fact]
    public void github_copilot_event_compatibility_assumption_is_documented()
    {
        var readmePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pi", "README.md");
        var readme = File.ReadAllText(readmePath);

        readme.ShouldContain("GitHub Copilot is assumed to be event-compatible");
        readme.ShouldContain("not a blocker for the v1 Pi harness implementation");
    }

    private static List<TranscriptEntry> LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pi", filename);
        var entries = new List<TranscriptEntry>();

        foreach (var line in File.ReadLines(path))
        {
            using var wrapper = JsonDocument.Parse(line);
            var root = wrapper.RootElement;
            var payloadJson = root.GetProperty("line").GetString()!;

            using var payload = JsonDocument.Parse(payloadJson);
            entries.Add(new TranscriptEntry(
                GetString(root, "phase"),
                root.GetProperty("stream").GetString()!,
                payload.RootElement.Clone()));
        }

        return entries;
    }

    private static List<HarnessEvent> ReplayFixture(string filename)
        => ReplayFixture(LoadFixture(filename));

    private static List<HarnessEvent> ReplayFixture(IEnumerable<TranscriptEntry> entries)
    {
        var mapper = new PiMapper("transcript-session", "pi");
        var events = new List<HarnessEvent>();

        foreach (var entry in entries.Where(static entry => entry.Stream == "stdout"))
        {
            var piEvent = DeserializePiEvent(entry.Payload);
            piEvent.ShouldNotBeNull();
            events.AddRange(mapper.Map(piEvent));
        }

        return events;
    }

    private static PiEvent DeserializePiEvent(JsonElement payload)
    {
        var orderedPayload = OrderDiscriminatorFirst(payload);
        var piEvent = JsonSerializer.Deserialize(orderedPayload.GetRawText(), PiJsonContext.Default.PiEvent);
        piEvent.ShouldNotBeNull();
        return piEvent;
    }

    private static JsonElement OrderDiscriminatorFirst(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type))
            return element.Clone();

        var properties = element.EnumerateObject().Where(static property => property.Name != "type").ToList();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("type");
            type.WriteTo(writer);

            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                var value = property.Name == "assistantMessageEvent"
                    ? OrderDiscriminatorFirst(property.Value)
                    : property.Value;
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void AssertTypeCounts(List<HarnessEvent> events, Dictionary<string, int> expected)
    {
        var actual = events
            .GroupBy(static evt => evt.Type)
            .ToDictionary(static group => group.Key, static group => group.Count());

        actual.Count.ShouldBe(expected.Count);
        foreach (var (eventType, count) in expected)
        {
            actual.ShouldContainKey(eventType);
            actual[eventType].ShouldBe(count);
        }
    }

    private static void AssertInOrder(List<HarnessEvent> events, params (string Label, Func<HarnessEvent, bool> Matches)[] expected)
    {
        var searchStart = 0;
        foreach (var (label, matches) in expected)
        {
            var index = -1;
            for (var i = searchStart; i < events.Count; i++)
            {
                if (!matches(events[i]))
                    continue;

                index = i;
                break;
            }

            index.ShouldNotBe(-1, $"Could not find ordered replay event: {label}");
            searchStart = index + 1;
        }
    }

    private static bool IsPiResponse(TranscriptEntry entry, string command, bool success)
    {
        return EventType(entry) == "response"
            && entry.Payload.TryGetProperty("command", out var commandElement)
            && commandElement.GetString() == command
            && entry.Payload.GetProperty("success").GetBoolean() == success;
    }

    private static bool ContainsProvider(HarnessEvent evt, string provider)
        => Payload(evt).GetRawText().Contains($"\"provider\":\"{provider}\"", StringComparison.Ordinal);

    private static string? EventType(TranscriptEntry entry)
    {
        return GetString(entry.Payload, "type");
    }

    private static bool IsStatus(HarnessEvent evt, string statusType)
        => evt.Type == EventTypes.SessionStatus && Status(evt).GetProperty("type").GetString() == statusType;

    private static bool IsPart(HarnessEvent evt, string partType)
        => evt.Type == EventTypes.MessagePartUpdated && Part(evt).GetProperty("type").GetString() == partType;

    private static bool IsTextDelta(HarnessEvent evt, string delta)
        => evt.Type == EventTypes.MessagePartDelta && Delta(evt) == delta;

    private static bool IsTool(HarnessEvent evt, string status)
        => IsPart(evt, "tool") && ToolStatus(evt) == status;

    private static JsonElement Payload(HarnessEvent evt)
    {
        evt.Payload.HasValue.ShouldBeTrue();
        return evt.Payload.Value;
    }

    private static JsonElement Status(HarnessEvent evt)
        => Payload(evt).GetProperty("status");

    private static string? StatusReason(HarnessEvent evt)
        => Status(evt).TryGetProperty("reason", out var reason) ? reason.GetString() : null;

    private static JsonElement Part(HarnessEvent evt)
        => Payload(evt).GetProperty("part");

    private static string PartText(HarnessEvent evt)
        => Part(evt).GetProperty("text").GetString() ?? string.Empty;

    private static string? PartReason(HarnessEvent evt)
        => Part(evt).TryGetProperty("reason", out var reason) ? reason.GetString() : null;

    private static string ToolName(HarnessEvent evt)
        => Part(evt).GetProperty("tool").GetString() ?? string.Empty;

    private static string ToolCallId(HarnessEvent evt)
        => Part(evt).GetProperty("callID").GetString() ?? string.Empty;

    private static string ToolStatus(HarnessEvent evt)
        => Part(evt).GetProperty("state").GetProperty("status").GetString() ?? string.Empty;

    private static JsonElement ToolInput(HarnessEvent evt)
        => Part(evt).GetProperty("state").GetProperty("input");

    private static bool HasObjectToolInput(HarnessEvent evt)
        => Part(evt).GetProperty("state").TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object;

    private static JsonElement ToolOutput(HarnessEvent evt)
        => Part(evt).GetProperty("state").GetProperty("output");

    private static string Delta(HarnessEvent evt)
        => Payload(evt).GetProperty("delta").GetString() ?? string.Empty;

    private static string? MessageRole(HarnessEvent evt)
        => Payload(evt).GetProperty("info").GetProperty("role").GetString();

    private static string? MessageAgent(HarnessEvent evt)
        => Payload(evt).GetProperty("info").TryGetProperty("agent", out var agent) ? agent.GetString() : null;

    private static long? LifecycleCompleted(HarnessEvent evt)
    {
        var time = Payload(evt).GetProperty("info").GetProperty("time");
        return time.TryGetProperty("completed", out var completed) ? completed.GetInt64() : null;
    }

    private static string ErrorMessage(HarnessEvent evt)
        => Payload(evt).GetProperty("message").GetString() ?? string.Empty;

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement)
            ? propertyElement.GetString()
            : null;
    }

    private readonly record struct TranscriptEntry(string? Phase, string Stream, JsonElement Payload);
}
