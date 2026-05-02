using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// File-driven scenario loader for the beta-tester rig. The runtime uses this when a session
/// is spawned with <see cref="Application.Harnesses.HarnessSpawnOptions.ScenarioId"/> set: it
/// loads <c>{baseDir}/{scenarioId}.json</c>, materialises a <see cref="TestScenario"/>, and
/// hands that to the per-session <see cref="TestHarnessSession"/>. If the file is missing or
/// malformed the loader returns a deterministic echo fallback so the session never hangs.
///
/// JSON shape:
/// <code>
/// {
///   "promptResponses": [
///     {
///       "events": [
///         { "type": "session.status",      "delayMs": 0,   "payload": { ... } },
///         { "type": "message.updated",     "delayMs": 0,   "payload": { ... } },
///         { "type": "message.part.updated","delayMs": 250, "payload": { ... } },
///         { "type": "session.idle",        "delayMs": 0,   "payload": { ... } }
///       ]
///     }
///   ]
/// }
/// </code>
/// Each entry in <c>promptResponses</c> is the event sequence emitted on the next user prompt
/// (FIFO). Payload JSON is forwarded to the harness verbatim; the model echo and adverse-mode
/// hooks described in the spec are the responsibility of the playbook author who writes the
/// JSON.
/// </summary>
public static class LiveScenarioHarness
{
    /// <summary>
    /// Resolve the directory scenario JSON files live in. The driver materialises playbook
    /// markdown into this directory at startup. Override with <c>FLEET_BETA_SCENARIO_DIR</c>;
    /// default is <c>tests/beta-harness/.runtime/scenarios</c> relative to the current
    /// working directory so a vanilla <c>dotnet run --project src/WeaveFleet.Api</c> from the
    /// repo root finds them.
    /// </summary>
    public static string ResolveScenarioDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("FLEET_BETA_SCENARIO_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;
        return Path.Combine(Directory.GetCurrentDirectory(), "tests", "beta-harness", ".runtime", "scenarios");
    }

    /// <summary>
    /// Load a scenario by id from <paramref name="scenarioDirectory"/>. Returns
    /// <see cref="FallbackEchoScenario"/> when the file is missing or malformed so the
    /// caller never has to reason about partial / null scenarios.
    /// </summary>
    public static TestScenario Load(string scenarioId, string scenarioDirectory)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
            return FallbackEchoScenario();

        var path = Path.Combine(scenarioDirectory, $"{scenarioId}.json");
        if (!File.Exists(path))
            return FallbackEchoScenario();

        try
        {
            using var stream = File.OpenRead(path);
            var doc = JsonDocument.Parse(stream);
            return Materialise(doc.RootElement);
        }
        catch
        {
            // Best-effort: malformed JSON falls back to echo so the session does not hang.
            return FallbackEchoScenario();
        }
    }

    /// <summary>
    /// Deterministic echo scenario used when a scenario id is unknown or a file is malformed.
    /// Emits busy → message.updated(user) → message.part.updated(user) → message.updated(assistant)
    /// → message.part.updated(assistant) → idle, with the assistant text fixed to a notice that
    /// no scenario was loaded so playbook authors notice the misconfiguration.
    /// </summary>
    public static TestScenario FallbackEchoScenario()
    {
        const string fallbackText =
            "[beta-harness fallback] No scenario loaded. " +
            "Set FLEET_BETA_SCENARIO_DIR or pass a known scenarioId on POST /api/sessions.";

        return new TestScenarioBuilder()
            .WithSimpleTextResponse(
                sessionId: "_placeholder_",
                messageId: $"fallback-{Guid.NewGuid():N}",
                text: fallbackText)
            .Build();
    }

    private static TestScenario Materialise(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return FallbackEchoScenario();

        if (!root.TryGetProperty("promptResponses", out var responsesEl)
            || responsesEl.ValueKind != JsonValueKind.Array)
            return FallbackEchoScenario();

        var queue = new Queue<IReadOnlyList<ScenarioEvent>>();
        foreach (var responseEl in responsesEl.EnumerateArray())
        {
            if (!responseEl.TryGetProperty("events", out var eventsEl)
                || eventsEl.ValueKind != JsonValueKind.Array)
                continue;

            var events = new List<ScenarioEvent>();
            foreach (var eventEl in eventsEl.EnumerateArray())
            {
                if (!eventEl.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String)
                    continue;

                var type = typeEl.GetString();
                if (string.IsNullOrEmpty(type)) continue;

                JsonElement? payload = eventEl.TryGetProperty("payload", out var payloadEl)
                    ? payloadEl.Clone()
                    : null;

                var delayMs = eventEl.TryGetProperty("delayMs", out var delayEl)
                    && delayEl.TryGetInt32(out var ms)
                    ? ms : 0;

                events.Add(new ScenarioEvent
                {
                    Event = new HarnessEvent
                    {
                        Type = type,
                        SessionId = "_placeholder_",
                        Timestamp = DateTimeOffset.UtcNow,
                        Payload = payload,
                    },
                    Delay = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
                });
            }

            if (events.Count > 0)
                queue.Enqueue(events);
        }

        return new TestScenario { PromptResponses = queue };
    }
}
