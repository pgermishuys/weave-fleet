using System.Collections.Immutable;
using System.Text.Json;
using global::NuCode.Sessions;
using Microsoft.Extensions.AI;
using WeaveFleet.Domain.Harnesses;
using HarnessMessagePart = WeaveFleet.Domain.Harnesses.MessagePart;
using NuCodeFilePart = global::NuCode.Sessions.FilePart;
using NuCodeMessagePart = global::NuCode.Sessions.MessagePart;
using NuCodeReasoningPart = global::NuCode.Sessions.ReasoningPart;
using NuCodeStepFinishPart = global::NuCode.Sessions.StepFinishPart;
using NuCodeTextPart = global::NuCode.Sessions.TextPart;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Maps between NuCode message/part types and WeaveFleet harness types.
/// </summary>
internal static class NuCodeMapper
{
    /// <summary>
    /// Converts NuCode session messages into <see cref="ChatMessage"/> instances
    /// suitable for passing to the LLM via <see cref="IChatClient"/>.
    /// </summary>
    public static IList<ChatMessage> ToChatMessages(IReadOnlyList<MessageWithParts> messages)
    {
        var result = new List<ChatMessage>(messages.Count);

        foreach (var mwp in messages)
        {
            var role = mwp.Message.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
            var contents = new List<AIContent>();

            foreach (var part in mwp.Parts)
            {
                switch (part)
                {
                    case NuCodeTextPart tp:
                        if (!tp.Ignored)
                        {
                            contents.Add(new TextContent(tp.Text));
                        }
                        break;

                    case ToolPart toolPart when toolPart.State is CompletedToolCallState completed:
                        // Tool use (assistant) → FunctionCallContent
                        if (role == ChatRole.Assistant)
                        {
                            var args = completed.Input.ToDictionary(
                                kvp => kvp.Key,
                                kvp => kvp.Value);
                            contents.Add(new FunctionCallContent(toolPart.CallId, toolPart.ToolName, args));
                        }
                        break;

                    case ToolPart toolPart when toolPart.State is ErrorToolCallState error:
                        if (role == ChatRole.Assistant)
                        {
                            var args = error.Input.ToDictionary(
                                kvp => kvp.Key,
                                kvp => kvp.Value);
                            contents.Add(new FunctionCallContent(toolPart.CallId, toolPart.ToolName, args));
                        }
                        break;
                }
            }

            if (contents.Count > 0)
            {
                result.Add(new ChatMessage(role, contents));
            }

            // Add tool results as separate user messages (standard ChatMessage format)
            foreach (var part in mwp.Parts)
            {
                if (part is ToolPart toolPart)
                {
                    switch (toolPart.State)
                    {
                        case CompletedToolCallState completed:
                            result.Add(new ChatMessage(ChatRole.Tool,
                            [
                                new FunctionResultContent(toolPart.CallId, completed.Output),
                            ]));
                            break;

                        case ErrorToolCallState error:
                            result.Add(new ChatMessage(ChatRole.Tool,
                            [
                                new FunctionResultContent(toolPart.CallId, $"Error: {error.Error}"),
                            ]));
                            break;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Converts NuCode session messages into <see cref="HarnessMessage"/> instances
    /// for the WeaveFleet UI layer.
    /// </summary>
    public static IReadOnlyList<HarnessMessage> ToHarnessMessages(IReadOnlyList<MessageWithParts> messages)
    {
        var result = new List<HarnessMessage>(messages.Count);

        foreach (var mwp in messages)
        {
            var parts = new List<HarnessMessagePart>();

            foreach (var part in mwp.Parts)
            {
                var mapped = MapToHarnessPart(part);
                if (mapped is not null)
                {
                    parts.Add(mapped);
                }
            }

            string? agent = null;
            string? modelId = null;

            if (mwp.Message is AssistantMessage am)
            {
                agent = am.Agent;
                modelId = am.ModelId;
            }
            else if (mwp.Message is UserMessage um)
            {
                agent = um.Agent;
            }

            result.Add(new HarnessMessage
            {
                Id = mwp.Message.Id.Value,
                Role = mwp.Message.Role == MessageRole.User ? "user" : "assistant",
                Parts = parts,
                Timestamp = mwp.Message.CreatedAt,
                Agent = agent,
                ModelId = modelId,
            });
        }

        return result;
    }

    private static HarnessMessagePart? MapToHarnessPart(NuCodeMessagePart part)
    {
        return part switch
        {
            NuCodeTextPart tp when !tp.Ignored =>
                new Domain.Harnesses.TextPart(tp.Text),

            NuCodeReasoningPart rp =>
                new Domain.Harnesses.ReasoningPart(rp.Text),

            ToolPart toolPart =>
                MapToolPart(toolPart),

            NuCodeFilePart fp =>
                new Domain.Harnesses.FilePart(fp.Id.Value, fp.Mime, fp.Url, fp.Filename),

            NuCodeStepFinishPart sfp =>
                new Domain.Harnesses.StepFinishPart(
                    Index: 0,
                    Reason: sfp.Reason,
                    Cost: (double)sfp.Cost,
                    TokensInput: sfp.Tokens.Input,
                    TokensOutput: sfp.Tokens.Output,
                    TokensReasoning: sfp.Tokens.Reasoning,
                    CompletedAt: null),

            _ => null,
        };
    }

    private static Domain.Harnesses.ToolUsePart MapToolPart(ToolPart toolPart)
    {
        var state = toolPart.State switch
        {
            PendingToolCallState => ToolUseState.Pending,
            RunningToolCallState => ToolUseState.Running,
            CompletedToolCallState => ToolUseState.Completed,
            ErrorToolCallState => ToolUseState.Error,
            _ => ToolUseState.Pending,
        };

        var args = JsonSerializer.SerializeToElement(
            toolPart.State.Input.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            NuCodeJsonContext.Default.DictionaryStringObject);

        return new Domain.Harnesses.ToolUsePart(toolPart.CallId, toolPart.ToolName, args, state);
    }
}
