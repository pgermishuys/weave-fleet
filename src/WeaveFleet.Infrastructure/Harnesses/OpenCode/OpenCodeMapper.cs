using System.Text.Json;
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
    /// </summary>
    internal static IReadOnlyList<HarnessMessage> ToHarnessMessages(
        IReadOnlyList<OpenCodeMessageWithParts> msgs)
    {
        var result = new HarnessMessage[msgs.Count];
        for (int i = 0; i < msgs.Count; i++)
        {
            result[i] = ToHarnessMessage(msgs[i]);
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
    /// </summary>
    internal static IReadOnlyList<HarnessAgent> ToHarnessAgents(
        IReadOnlyList<OpenCodeAgentInfo> agents)
    {
        var result = new HarnessAgent[agents.Count];
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            result[i] = new HarnessAgent(a.Name, a.Description, a.Mode);
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
}
