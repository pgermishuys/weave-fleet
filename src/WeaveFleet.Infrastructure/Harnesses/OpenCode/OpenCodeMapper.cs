using System.Text.Json;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Maps OpenCode API DTOs to Weave Fleet domain types.
/// </summary>
internal static class OpenCodeMapper
{
    /// <summary>
    /// Maps an <see cref="OpenCodeMessageWithParts"/> to a <see cref="HarnessMessage"/>.
    /// </summary>
    internal static HarnessMessage ToHarnessMessage(OpenCodeMessageWithParts msg)
    {
        var parts = new List<MessagePart>();

        foreach (var part in msg.Parts)
        {
            switch (part)
            {
                case OpenCodeTextPart textPart when textPart.Text is not null:
                    parts.Add(new TextPart(textPart.Text));
                    break;

                case OpenCodeToolPart toolPart:
                    var toolState = toolPart.State switch
                    {
                        OpenCodeToolPending => ToolUseState.Pending,
                        OpenCodeToolRunning => ToolUseState.Running,
                        OpenCodeToolCompleted => ToolUseState.Completed,
                        OpenCodeToolError => ToolUseState.Error,
                        _ => ToolUseState.Pending,
                    };
                    parts.Add(new ToolUsePart(
                        ToolCallId: toolPart.CallId ?? toolPart.Id,
                        ToolName: toolPart.Tool ?? string.Empty,
                        Arguments: toolPart.State switch
                        {
                            OpenCodeToolPending p => p.Input ?? default,
                            OpenCodeToolRunning r => r.Input ?? default,
                            OpenCodeToolCompleted c => c.Input ?? default,
                            OpenCodeToolError e => e.Input ?? default,
                            _ => default,
                        },
                        State: toolState));
                    break;

                case OpenCodeReasoningPart reasoning when reasoning.Text is not null:
                    parts.Add(new TextPart($"[reasoning] {reasoning.Text}"));
                    break;
            }
        }

        return new HarnessMessage
        {
            Id = msg.Info.Id,
            Role = msg.Info.Role,
            Parts = parts,
            Timestamp = DateTimeOffsetFromUnixMs(msg.Info.Time.Created),
        };
    }

    /// <summary>
    /// Maps a list of <see cref="OpenCodeMessageWithParts"/> to <see cref="HarnessMessage"/> instances.
    /// Messages with an unrecognised role (neither "user" nor "assistant") are silently skipped.
    /// </summary>
    internal static IReadOnlyList<HarnessMessage> ToHarnessMessages(
        IReadOnlyList<OpenCodeMessageWithParts> msgs)
    {
        var result = new List<HarnessMessage>(msgs.Count);
        for (int i = 0; i < msgs.Count; i++)
        {
            var msg = msgs[i];
            // Skip messages whose role discriminator was missing or unrecognised —
            // they deserialised as the base OpenCodeMessageInfo (Role == "unknown").
            if (msg.Info.Role is not ("user" or "assistant"))
                continue;

            result.Add(ToHarnessMessage(msg));
        }
        return result;
    }

    /// <summary>
    /// Maps an <see cref="OpenCodeSseEvent"/> to a <see cref="HarnessEvent"/>.
    /// </summary>
    internal static HarnessEvent ToHarnessEvent(OpenCodeSseEvent evt, string? sessionId)
    {
        // Try to extract sessionId from properties if present
        string resolvedSession = sessionId ?? string.Empty;
        if (evt.Properties.ValueKind == JsonValueKind.Object
            && evt.Properties.TryGetProperty("sessionId", out var sid)
            && sid.ValueKind == JsonValueKind.String)
        {
            resolvedSession = sid.GetString() ?? resolvedSession;
        }

        return new HarnessEvent
        {
            Type = evt.Type,
            SessionId = resolvedSession,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = evt.Properties,
        };
    }

    /// <summary>
    /// Maps OpenCode agent info to <see cref="HarnessAgent"/> instances.
    /// Agents with a null name are skipped — a nameless agent cannot be meaningfully referenced.
    /// </summary>
    internal static IReadOnlyList<HarnessAgent> ToHarnessAgents(
        IReadOnlyList<OpenCodeAgentInfo> agents)
    {
        var result = new List<HarnessAgent>(agents.Count);
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Name is null) continue;
            result.Add(new HarnessAgent(a.Name, a.Description, a.Mode));
        }
        return result;
    }

    /// <summary>
    /// Maps OpenCode providers response to <see cref="HarnessProvider"/> instances.
    /// </summary>
    internal static IReadOnlyList<HarnessProvider> ToHarnessProviders(
        OpenCodeProvidersResponse response)
    {
        var result = new HarnessProvider[response.All.Count];
        for (int i = 0; i < response.All.Count; i++)
        {
            var p = response.All[i];
            var models = p.Models
                .Select(m => new HarnessModel(m.Id, m.Name ?? m.Id))
                .ToList();
            result[i] = new HarnessProvider(p.Id, p.Name ?? p.Id, models);
        }
        return result;
    }

    /// <summary>Converts a Unix millisecond timestamp to <see cref="DateTimeOffset"/>.</summary>
    internal static DateTimeOffset DateTimeOffsetFromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>
    /// Attempts to extract token/cost data from an OpenCode SSE event.
    /// Only processes <c>message.updated</c> events where the Properties contains
    /// an <c>"info"</c> field wrapping an assistant message with token data.
    /// Returns <c>null</c> for all other events, or on any parse failure.
    /// This is a side-channel extraction — it must never throw or block.
    /// </summary>
    /// <remarks>
    /// The <c>message.created</c> event type is intentionally excluded.
    /// Its Properties structure has not been verified against a contract fixture,
    /// so it is unsafe to deserialize. See analytics-store.md § Task 14 for details.
    /// </remarks>
    internal static TokenEventData? TryExtractTokenEvent(
        OpenCodeSseEvent evt,
        string? sessionId,
        string? projectId,
        string? projectName,
        string? workspaceDirectory)
    {
        // Only process message.updated events
        if (evt.Type is not "message.updated")
            return null;

        try
        {
            if (evt.Properties.ValueKind != JsonValueKind.Object)
                return null;

            // The message.updated Properties wraps the message under "info":
            //   { "info": { "id": "...", "role": "assistant", ... } }
            // Confirmed by contract fixture: tests/contracts/opencode-to-fleet-events.json
            if (!evt.Properties.TryGetProperty("info", out var infoEl))
                return null;

            // Check "role" directly — avoid polymorphic deserialization of the abstract base type,
            // which requires the discriminator to be the first property in some STJ configurations.
            if (!infoEl.TryGetProperty("role", out var roleEl)
                || roleEl.GetString() is not "assistant")
                return null;

            var assistant = infoEl.Deserialize<OpenCodeAssistantMessage>(OpenCodeJsonOptions.Default);
            if (assistant is null)
                return null;

            // Skip messages with no token data
            if (assistant.Tokens is null && assistant.Cost is null)
                return null;

            var tokens = assistant.Tokens;
            var tokensTotal = tokens?.Total ?? 0;
            var cost = assistant.Cost ?? 0;

            var estimatedCost = ModelPricing.EstimateCost(
                assistant.ModelId,
                tokens?.Input ?? 0,
                tokens?.Output ?? 0,
                tokens?.Reasoning ?? 0,
                tokens?.Cache?.Read ?? 0);

            return new TokenEventData(
                EventId: $"{sessionId}:{assistant.Id}",
                SessionId: sessionId ?? "",
                ProjectId: projectId,
                ProjectName: projectName,
                WorkspaceDirectory: workspaceDirectory,
                ModelId: assistant.ModelId,
                ProviderId: assistant.ProviderId,
                TokensInput: tokens?.Input ?? 0,
                TokensOutput: tokens?.Output ?? 0,
                TokensReasoning: tokens?.Reasoning ?? 0,
                TokensCacheRead: tokens?.Cache?.Read ?? 0,
                TokensCacheWrite: tokens?.Cache?.Write ?? 0,
                TokensTotal: tokensTotal,
                Cost: cost,
                EstimatedCost: estimatedCost,
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(assistant.Time.Created));
        }
        catch
        {
            // Silent failure — analytics must never crash sessions
            return null;
        }
    }
}
