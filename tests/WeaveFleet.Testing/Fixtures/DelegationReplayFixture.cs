using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fixtures;

/// <summary>
/// A hand-curated replay fixture derived from the real captured SSE log at
/// <c>.weave/learnings/opencode-delegated-session.log</c>.
/// Provides ~25 key events that cover the full delegation lifecycle:
/// parent busy → task pending → session.created (child) → child work → child idle →
/// task completed → parent final response → parent idle.
/// </summary>
public static class DelegationReplayFixture
{
    // -----------------------------------------------------------------------
    // Well-known identifiers
    // -----------------------------------------------------------------------

    public const string ParentSessionId = "ses_24b6b4488ffe5Z4wpXWkT4mQgG";
    public const string ChildSessionId = "ses_24b6b0ba6ffeMvGD20Kz1pUCG3";
    public const string ParentModelId = "gpt-5.4";
    public const string ChildModelId = "claude-haiku-4.5";
    public const string ParentProviderId = "github-copilot";
    public const string ChildProviderId = "github-copilot";
    public const string ParentAgent = "Loom (Main Orchestrator)";
    public const string ChildAgent = "thread";
    public const string ParentToolCallId = "call_M0wkGVVWC43h5DZM8SA16uwE";
    public const string ChildTitle = "Read Program.cs (@thread subagent)";
    public const string ParentUserMessageId = "msg_db494c240001mrl5KRwPZ60lId";
    public const string ParentAssistantMessageId = "msg_db494eb570016tFoOACNsv0MIp";
    public const string ChildUserMessageId = "msg_db494f45e001oqQtf9C7GvhYFt";
    public const string ChildAssistantMessageId = "msg_db494f4650014KIp5saXTkg3ng";

    // -----------------------------------------------------------------------
    // Raw SSE lines (curated ~25-event subset)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all SSE data lines in chronological order, formatted as
    /// <c>data: {JSON}</c>. Each string is one SSE event line.
    /// </summary>
    public static IReadOnlyList<string> GetSseLines() =>
    [
        // 1. Parent user message arrives
        $"data: {{\"type\":\"message.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"info\":{{\"id\":\"{ParentUserMessageId}\",\"role\":\"user\",\"sessionID\":\"{ParentSessionId}\",\"time\":{{\"created\":1776851141184}},\"agent\":\"{ParentAgent}\",\"model\":{{\"providerID\":\"{ParentProviderId}\",\"modelID\":\"{ParentModelId}\"}}}}}}}}",

        // 2. Parent session busy
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"status\":{{\"type\":\"busy\"}}}}}}",

        // 3. Parent assistant message created (initial)
        $"data: {{\"type\":\"message.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"info\":{{\"id\":\"{ParentAssistantMessageId}\",\"parentID\":\"{ParentUserMessageId}\",\"role\":\"assistant\",\"agent\":\"{ParentAgent}\",\"modelID\":\"{ParentModelId}\",\"providerID\":\"{ParentProviderId}\",\"time\":{{\"created\":1776851151703}},\"sessionID\":\"{ParentSessionId}\"}}}}}}",

        // 4. Parent busy again during LLM work
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"status\":{{\"type\":\"busy\"}}}}}}",

        // 5. Task tool pending
        $"data: {{\"type\":\"message.part.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"part\":{{\"id\":\"prt_db494f00f001VswKPMB3LL32oy\",\"messageID\":\"{ParentAssistantMessageId}\",\"sessionID\":\"{ParentSessionId}\",\"type\":\"tool\",\"tool\":\"task\",\"callID\":\"{ParentToolCallId}\",\"state\":{{\"status\":\"pending\",\"input\":{{}},\"raw\":\"\"}}}},\"time\":1776851152911}}}}",

        // 6. Task tool running (before session.created — same batch)
        $"data: {{\"type\":\"message.part.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"part\":{{\"type\":\"tool\",\"tool\":\"task\",\"callID\":\"{ParentToolCallId}\",\"state\":{{\"status\":\"running\",\"input\":{{\"description\":\"Read Program.cs\",\"subagent_type\":\"thread\",\"prompt\":\"Locate the relevant Program.cs file.\"}},\"raw\":\"\",\"time\":{{\"start\":1776851154005}}}},\"id\":\"prt_db494f00f001VswKPMB3LL32oy\",\"sessionID\":\"{ParentSessionId}\",\"messageID\":\"{ParentAssistantMessageId}\"}},\"time\":1776851154005}}}}",

        // 7. session.created for child — carries parentID
        $"data: {{\"type\":\"session.created\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"info\":{{\"id\":\"{ChildSessionId}\",\"slug\":\"happy-eagle\",\"version\":\"1.14.20\",\"projectID\":\"global\",\"directory\":\"/workspace\",\"parentID\":\"{ParentSessionId}\",\"title\":\"{ChildTitle}\",\"time\":{{\"created\":1776851154010,\"updated\":1776851154010}}}}}}}}",

        // 8. Task tool running with child session metadata (same batch as session.created)
        $"data: {{\"type\":\"message.part.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"part\":{{\"type\":\"tool\",\"tool\":\"task\",\"callID\":\"{ParentToolCallId}\",\"state\":{{\"title\":\"Read Program.cs\",\"metadata\":{{\"sessionId\":\"{ChildSessionId}\",\"model\":{{\"providerID\":\"{ChildProviderId}\",\"modelID\":\"{ChildModelId}\"}}}},\"status\":\"running\",\"input\":{{\"description\":\"Read Program.cs\",\"subagent_type\":\"thread\",\"prompt\":\"Locate the relevant Program.cs file.\"}},\"time\":{{\"start\":1776851154013}}}},\"id\":\"prt_db494f00f001VswKPMB3LL32oy\",\"sessionID\":\"{ParentSessionId}\",\"messageID\":\"{ParentAssistantMessageId}\"}},\"time\":1776851154013}}}}",

        // 9. Child user message arrives (immediately after session.created — race condition test)
        $"data: {{\"type\":\"message.updated\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"info\":{{\"id\":\"{ChildUserMessageId}\",\"role\":\"user\",\"sessionID\":\"{ChildSessionId}\",\"time\":{{\"created\":1776851154015}},\"agent\":\"{ChildAgent}\",\"model\":{{\"providerID\":\"{ChildProviderId}\",\"modelID\":\"{ChildModelId}\"}}}}}}}}",

        // 10. Child session busy
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"status\":{{\"type\":\"busy\"}}}}}}",

        // 11. Parent session still busy while child works
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"status\":{{\"type\":\"busy\"}}}}}}",

        // 12. Child assistant message created
        $"data: {{\"type\":\"message.updated\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"info\":{{\"id\":\"{ChildAssistantMessageId}\",\"parentID\":\"{ChildUserMessageId}\",\"role\":\"assistant\",\"agent\":\"{ChildAgent}\",\"modelID\":\"{ChildModelId}\",\"providerID\":\"{ChildProviderId}\",\"time\":{{\"created\":1776851154021}},\"sessionID\":\"{ChildSessionId}\"}}}}}}",

        // 13. Child using a tool (read_file)
        $"data: {{\"type\":\"message.part.updated\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"part\":{{\"id\":\"prt_child_tool_1\",\"messageID\":\"{ChildAssistantMessageId}\",\"sessionID\":\"{ChildSessionId}\",\"type\":\"tool\",\"tool\":\"read\",\"callID\":\"call_child_read_1\",\"state\":{{\"status\":\"running\",\"input\":{{\"filePath\":\"/workspace/Program.cs\"}}}}}},\"time\":1776851156000}}}}",

        // 14. Child tool completed
        $"data: {{\"type\":\"message.part.updated\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"part\":{{\"id\":\"prt_child_tool_1\",\"messageID\":\"{ChildAssistantMessageId}\",\"sessionID\":\"{ChildSessionId}\",\"type\":\"tool\",\"tool\":\"read\",\"callID\":\"call_child_read_1\",\"state\":{{\"status\":\"completed\",\"input\":{{\"filePath\":\"/workspace/Program.cs\"}},\"output\":\"var builder = WebApplication.CreateBuilder(args);\"}}}},\"time\":1776851157000}}}}",

        // 15. Child final message updated (with completion text)
        $"data: {{\"type\":\"message.updated\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"info\":{{\"id\":\"{ChildAssistantMessageId}\",\"parentID\":\"{ChildUserMessageId}\",\"role\":\"assistant\",\"agent\":\"{ChildAgent}\",\"modelID\":\"{ChildModelId}\",\"providerID\":\"{ChildProviderId}\",\"time\":{{\"created\":1776851154021,\"completed\":1776851167000}},\"tokens\":{{\"total\":500,\"input\":100,\"output\":400,\"reasoning\":0,\"cache\":{{\"read\":0,\"write\":0}}}},\"sessionID\":\"{ChildSessionId}\",\"finish\":\"end-turn\"}}}}}}",

        // 16. Child session.status idle
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\",\"status\":{{\"type\":\"idle\"}}}}}}",

        // 17. Child session.idle
        $"data: {{\"type\":\"session.idle\",\"properties\":{{\"sessionID\":\"{ChildSessionId}\"}}}}",

        // 18. Task tool completed on parent
        $"data: {{\"type\":\"message.part.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"part\":{{\"type\":\"tool\",\"tool\":\"task\",\"callID\":\"{ParentToolCallId}\",\"state\":{{\"status\":\"completed\",\"input\":{{\"description\":\"Read Program.cs\",\"subagent_type\":\"thread\",\"prompt\":\"Locate the relevant Program.cs file.\"}},\"output\":\"Program.cs is a standard ASP.NET Core entry point.\",\"metadata\":{{\"sessionId\":\"{ChildSessionId}\",\"model\":{{\"providerID\":\"{ChildProviderId}\",\"modelID\":\"{ChildModelId}\"}},\"truncated\":false}},\"title\":\"Read Program.cs\",\"time\":{{\"start\":1776851154013,\"end\":1776851167525}}}},\"id\":\"prt_db494f00f001VswKPMB3LL32oy\",\"sessionID\":\"{ParentSessionId}\",\"messageID\":\"{ParentAssistantMessageId}\"}},\"time\":1776851167525}}}}",

        // 19. Parent final message updated
        $"data: {{\"type\":\"message.updated\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"info\":{{\"id\":\"{ParentAssistantMessageId}\",\"parentID\":\"{ParentUserMessageId}\",\"role\":\"assistant\",\"agent\":\"{ParentAgent}\",\"modelID\":\"{ParentModelId}\",\"providerID\":\"{ParentProviderId}\",\"time\":{{\"created\":1776851151703,\"completed\":1776851167527}},\"tokens\":{{\"total\":9578,\"input\":268,\"output\":94,\"reasoning\":0,\"cache\":{{\"write\":0,\"read\":9216}}}},\"sessionID\":\"{ParentSessionId}\",\"finish\":\"tool-calls\"}}}}}}",

        // 20. Parent session busy (still processing after child completes)
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"status\":{{\"type\":\"busy\"}}}}}}",

        // 21. Parent session idle (synthetic — the log was manually terminated before parent went idle)
        $"data: {{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{ParentSessionId}\",\"status\":{{\"type\":\"idle\"}}}}}}",
    ];

    // -----------------------------------------------------------------------
    // Parsed HarnessEvent stream
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all fixture events as parsed <see cref="HarnessEvent"/> records,
    /// ready to push via <c>TestHarnessSession.PushEventAsync()</c>.
    /// Parent events carry the parent session ID; child events carry the child session ID.
    /// </summary>
    public static IReadOnlyList<HarnessEvent> GetHarnessEvents() =>
        GetSseLines()
            .Select(ParseSseLine)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Returns only the parent session events (sessionID == <see cref="ParentSessionId"/>).
    /// </summary>
    public static IReadOnlyList<HarnessEvent> GetParentHarnessEvents() =>
        GetHarnessEvents()
            .Where(e => e.SessionId == ParentSessionId)
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Returns only the child session events (sessionID == <see cref="ChildSessionId"/>).
    /// </summary>
    public static IReadOnlyList<HarnessEvent> GetChildHarnessEvents() =>
        GetHarnessEvents()
            .Where(e => e.SessionId == ChildSessionId)
            .ToList()
            .AsReadOnly();

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private static HarnessEvent? ParseSseLine(string sseLine)
    {
        var json = sseLine.StartsWith("data: ", StringComparison.Ordinal)
            ? sseLine["data: ".Length..]
            : sseLine;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;

            var type = typeEl.GetString() ?? string.Empty;

            string sessionId = string.Empty;
            if (root.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                if (propsEl.TryGetProperty("sessionID", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
                    sessionId = sidEl.GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(sessionId) && propsEl.TryGetProperty("info", out var infoEl)
                    && infoEl.TryGetProperty("sessionID", out var infoSidEl))
                    sessionId = infoSidEl.GetString() ?? string.Empty;
            }

            // Clone the properties into a stable JsonElement
            var payload = root.TryGetProperty("properties", out var payloadEl)
                ? JsonSerializer.Deserialize<JsonElement>(payloadEl.GetRawText())
                : default(JsonElement?);

            return new HarnessEvent
            {
                Type = type,
                SessionId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = payload,
            };
        }
        catch
        {
            return null;
        }
    }
}
