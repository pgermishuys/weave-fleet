using System.Text.Json;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Turns V2's stored messages (<c>GET /api/session/{id}/message</c>) into Fleet's <see cref="HarnessMessage"/>s, for
/// a reopened session. Parts get the ids <see cref="OpenCode2Mapper"/> gives them live, so a reopened session shows
/// the same parts, and a part still streaming when it's reopened updates in place.
/// </summary>
internal static class OpenCode2History
{
    /// <summary>The messages Fleet shows, in the order given; V2's other message types (idle, shell, compaction, …) are left out.</summary>
    /// <remarks>
    /// A <c>synthetic</c> message is V2 talking to the model: only a background completion is news for the user, and
    /// it shows as a message of the session's own, the way it does live.
    /// </remarks>
    public static IReadOnlyList<HarnessMessage> ToHarnessMessages(IEnumerable<OpenCode2Message> messages)
        => messages.Select(ToHarnessMessage).OfType<HarnessMessage>().ToList();

    private static HarnessMessage? ToHarnessMessage(OpenCode2Message message)
    {
        if (message.Id is not { } id)
            return null;

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.Time?.Created ?? 0);
        return message.Type switch
        {
            // Fleet shows a prompt's text as part 0, under the id it sent the prompt with, then its attachments.
            "user" => new HarnessMessage
            {
                Id = id,
                Role = "user",
                Parts = UserParts(id, message),
                Timestamp = timestamp,
            },
            "assistant" => new HarnessMessage
            {
                Id = id,
                Role = "assistant",
                Parts = AssistantParts(id, message),
                Timestamp = timestamp,
                Agent = message.Agent,
                ModelId = message.Model?.Id,
                Finish = message.Finish,
                Error = TurnFailure(message.Error),
            },
            "synthetic" when IsBackgroundNotice(message.Metadata) => new HarnessMessage
            {
                Id = id,
                Role = OpenCode2Mapper.NoticeRole,
                Parts = [new TextPart(message.Text ?? string.Empty) { PartId = OpenCode2Mapper.PartId(id, "text", 0) }],
                Timestamp = timestamp,
            },
            _ => null,
        };
    }

    /// <summary>Whether a synthetic message says background work finished, rather than instructing the model.</summary>
    private static bool IsBackgroundNotice(JsonElement metadata)
        => metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("source", out var source)
            && source.ValueKind == JsonValueKind.String
            && source.GetString() is "shell" or OpenCode2Mapper.SubagentTool;

    /// <summary>A prompt's text, then each attachment as a file part, the way Fleet showed the prompt when it was sent.</summary>
    private static List<MessagePart> UserParts(string messageId, OpenCode2Message message)
    {
        var parts = new List<MessagePart> { new TextPart(message.Text ?? string.Empty) { PartId = OpenCode2Mapper.PartId(messageId, "text", 0) } };
        parts.AddRange((message.Files ?? [])
            .Where(f => !string.IsNullOrEmpty(f.Data))
            .Select((file, index) =>
            {
                var mime = file.Mime ?? "application/octet-stream";
                return new FilePart(PromptFilePartId(messageId, index), mime, $"data:{mime};base64,{file.Data}", file.Name);
            }));
        return parts;
    }

    /// <summary>The id Fleet gives attachment <paramref name="index"/> of a prompt.</summary>
    internal static string PromptFilePartId(string messageId, int index) => $"{messageId}-file-{index}";

    private static List<MessagePart> AssistantParts(string messageId, OpenCode2Message message)
    {
        var parts = new List<MessagePart>();
        int text = 0, reasoning = 0;
        foreach (var content in message.Content ?? [])
        {
            switch (content.Type)
            {
                case "text":
                    parts.Add(new TextPart(content.Text ?? string.Empty) { PartId = OpenCode2Mapper.PartId(messageId, "text", text++) });
                    break;
                case "reasoning":
                    parts.Add(new ReasoningPart(content.Text ?? string.Empty) { PartId = OpenCode2Mapper.PartId(messageId, "reasoning", reasoning++) });
                    break;
                case "tool" when content is { Id: { } callId, Name: { } name, State: { } state }:
                    parts.Add(ToolPart(messageId, callId, name, state));
                    parts.AddRange((state.Content ?? [])
                        .Where(c => c.Type == "file" && c.Uri is not null)
                        .Select((file, index) => new FilePart(
                            OpenCode2Mapper.ToolFilePartId(messageId, callId, index),
                            file.Mime ?? "application/octet-stream",
                            file.Uri!,
                            file.Name)));
                    break;
            }
        }

        // A finished step reports what it used; one still running reports it when it ends.
        if (message.Time?.Completed is { } completed)
        {
            parts.Add(new StepFinishPart(
                Index: 0,
                message.Finish,
                message.Cost ?? 0,
                message.Tokens?.Input ?? 0,
                message.Tokens?.Output ?? 0,
                message.Tokens?.Reasoning ?? 0,
                completed));
        }

        return parts;
    }

    private static ToolUsePart ToolPart(string messageId, string callId, string name, OpenCode2ToolState state)
    {
        var stored = OpenCode2Mapper.HistoryToolStatus(state.Status);
        // A call V2 moved into the background is done as a call, but its work isn't: it keeps its card running.
        var status = OpenCode2Mapper.ToolStatus(state.Status, state.Metadata);
        var output = stored is "completed" or "error" ? OpenCode2Mapper.ToolOutput(state.Content) : null;
        return new ToolUsePart(
            callId,
            name,
            // While V2 is still streaming the input it's text, not yet arguments.
            state.Input.ValueKind == JsonValueKind.Object ? state.Input.Clone() : default,
            status switch
            {
                "running" => ToolUseState.Running,
                "completed" => ToolUseState.Completed,
                "error" => ToolUseState.Error,
                _ => ToolUseState.Pending,
            })
        {
            PartId = OpenCode2Mapper.ToolPartId(messageId, callId),
            Background = status == "running" && stored == "completed",
            Output = output is null ? null : JsonSerializer.SerializeToElement(output, OpenCode2JsonContext.Default.String),
            Error = stored == "error" ? state.Error?.Message ?? state.Error?.Type : null,
            Metadata = state.Metadata.ValueKind == JsonValueKind.Object ? state.Metadata.Clone() : null,
        };
    }

    /// <summary>
    /// Why a step failed, as the failure card shows it. An interrupted step (<c>aborted</c>: the user stopped it or
    /// dismissed a question) isn't a failure, and live it shows none either.
    /// </summary>
    private static TurnError? TurnFailure(OpenCode2StructuredError? error)
        => error is { Type: not "aborted" }
            ? new TurnError { Name = error.Type ?? "Error", Message = error.Message ?? error.Type ?? "The step failed." }
            : null;
}
