using System.Globalization;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.TerminalSessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Reads a Claude Code session transcript (<c>~/.claude/projects/&lt;folder&gt;/&lt;session id&gt;.jsonl</c>) into Fleet's
/// messages. The rules are the ones Anthropic's Agent SDK reads it by (<c>getSessionMessages</c>): follow the
/// <c>parentUuid</c> chain back from the newest entry, so a branch left by a rewind (Esc Esc) isn't in the conversation,
/// leave out side chains and meta entries, and keep the user and assistant ones. A compaction starts a chain of its own;
/// its boundary names the line it continues (<c>logicalParentUuid</c>), which is followed too, so the history before it
/// stays.
/// </summary>
/// <remarks>
/// Every message's id is made from the uuid of the entry it starts at, so reading the same transcript again, or a longer
/// one, gives the messages already saved the ids they have. A Claude message is one line per content block, all with the
/// same message id; they are one Fleet message here, as the live stream makes them.
/// </remarks>
internal static class ClaudeTranscriptReader
{
    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.Ordinal)
    {
        "attachment", "system", "file-history-snapshot", "mode", "permission-mode", "atis-latch", "ai-title", "custom-title",
        "agent-name", "last-prompt", "summary", "cost-state", "queue-operation", "progress", "pr-link", "tag",
        "api-request", "api-request-shape", "api-request-blob", "content-replacement", "worktree-state", "tools-used",
    };

    /// <summary>Reads <paramref name="lines"/>, the transcript's lines. A line that isn't JSON (a write cut off) is skipped.</summary>
    public static TerminalTranscript Read(IEnumerable<string> lines)
    {
        var entries = new List<JsonElement>();
        var byUuid = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonElement entry;
            try
            {
                using var document = JsonDocument.Parse(line);
                entry = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            entries.Add(entry);
            if (String(entry, "uuid") is { } uuid)
                byUuid[uuid] = entry;
        }

        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        string? title = null;
        string? summary = null;
        string? aiTitle = null;
        string? folder = null;
        foreach (var entry in entries)
        {
            switch (String(entry, "type"))
            {
                case "custom-title":
                    title = String(entry, "customTitle") ?? title;
                    break;
                case "ai-title":
                    aiTitle = String(entry, "aiTitle") ?? aiTitle;
                    break;
                case "summary":
                    summary = String(entry, "summary") ?? summary;
                    break;
            }

            folder ??= String(entry, "cwd");
        }

        title = Named(title) ?? Named(aiTitle) ?? Named(summary);

        // The newest entry that is part of a conversation: the end of the chain the SDK follows.
        JsonElement? leaf = null;
        foreach (var entry in entries)
        {
            if (String(entry, "uuid") is not null && !Bool(entry, "isSidechain")
                && String(entry, "type") is "user" or "assistant" or "system" or "attachment")
            {
                leaf = entry;
            }
        }

        var chain = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = leaf;
        while (current is { } node && String(node, "uuid") is { } id && seen.Add(id))
        {
            chain.Add(node);
            var parent = String(node, "parentUuid") ?? String(node, "logicalParentUuid");
            current = parent is not null && byUuid.TryGetValue(parent, out var next) ? next : null;
        }

        chain.Reverse();

        var builder = new ConversationBuilder(unknown);
        foreach (var entry in chain)
            builder.Add(entry);

        // Types that never reach the chain still tell us when a format changes: note any we have no rule for.
        foreach (var entry in entries)
        {
            if (String(entry, "type") is { } type && type is not ("user" or "assistant") && !IgnoredTypes.Contains(type))
                unknown.Add($"entry:{type}");
        }

        var messages = builder.Messages;
        var firstPrompt = messages.FirstOrDefault(m => m.Role == "user")?.TextContent;
        return new TerminalTranscript(messages, title, Named(firstPrompt), folder, [.. unknown]);
    }

    private static string? Named(string? text)
    {
        var trimmed = text?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= 120 ? trimmed : trimmed[..119].TrimEnd() + "…";
    }

    private sealed class ConversationBuilder(SortedSet<string> unknown)
    {
        private readonly List<HarnessMessage> _messages = [];
        private readonly Dictionary<string, int> _callMessages = new(StringComparer.Ordinal);
        // The calls as Claude Code wrote them: an edit's diff is worked out from its own names.
        private readonly Dictionary<string, (string Name, JsonElement Input)> _rawCalls = new(StringComparer.Ordinal);
        private string? _claudeMessageId;
        private long _lastTimestamp;
        private int _lastCounter = -1;

        public IReadOnlyList<HarnessMessage> Messages => _messages;

        public void Add(JsonElement entry)
        {
            if (Bool(entry, "isSidechain"))
                return;

            switch (String(entry, "type"))
            {
                case "user":
                    AddUser(entry);
                    break;
                case "assistant":
                    AddAssistant(entry);
                    break;
                case "system" when String(entry, "subtype") == "compact_boundary":
                    AddCompaction(entry);
                    break;
            }
        }

        private void AddUser(JsonElement entry)
        {
            // Meta entries are Claude Code's own notes to the model; a compaction's summary is shown behind its divider.
            if (Bool(entry, "isMeta") || Bool(entry, "isCompactSummary"))
                return;

            _claudeMessageId = null;
            if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                return;
            }

            var parts = new List<MessagePart>();
            if (content.ValueKind == JsonValueKind.String)
            {
                if (content.GetString() is { Length: > 0 } text)
                    parts.Add(new TextPart(text));
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    switch (String(block, "type"))
                    {
                        case "text":
                            if (String(block, "text") is { Length: > 0 } blockText)
                                parts.Add(new TextPart(blockText));
                            break;
                        case "image":
                            if (Image(block, parts.Count) is { } image)
                                parts.Add(image);
                            break;
                        case "tool_result":
                            FinishCall(block, entry);
                            break;
                        case { } other:
                            unknown.Add($"user-block:{other}");
                            break;
                    }
                }
            }

            if (parts.Count == 0)
                return;

            var id = NextId(entry);
            _messages.Add(new HarnessMessage
            {
                Id = id,
                Role = "user",
                Parts = Stamp(id, parts),
                Timestamp = Time(entry) ?? DateTimeOffset.UnixEpoch,
            });
        }

        private void AddAssistant(JsonElement entry)
        {
            if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || message.TryGetProperty("content", out var content) is false)
            {
                return;
            }

            var claudeId = String(message, "id");
            var parts = new List<MessagePart>();
            if (content.ValueKind == JsonValueKind.String)
            {
                if (content.GetString() is { Length: > 0 } text)
                    parts.Add(new TextPart(text));
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    switch (String(block, "type"))
                    {
                        case "text":
                            if (String(block, "text") is { Length: > 0 } blockText)
                                parts.Add(new TextPart(blockText));
                            break;
                        case "thinking":
                            if (String(block, "thinking") is { Length: > 0 } thinking)
                                parts.Add(new ReasoningPart(thinking));
                            break;
                        case "redacted_thinking":
                            break;
                        case "tool_use":
                            parts.Add(ToolCall(block));
                            break;
                        case { } other:
                            unknown.Add($"assistant-block:{other}");
                            break;
                    }
                }
            }

            if (parts.Count == 0)
                return;

            var model = String(message, "model");

            // One Claude message arrives as a line per block: they are one message here.
            if (claudeId is not null && claudeId == _claudeMessageId && _messages.Count > 0 && _messages[^1].Role == "assistant")
            {
                var index = _messages.Count - 1;
                var existing = _messages[index];
                var all = existing.Parts.ToList();
                foreach (var part in Stamp(existing.Id, parts, all.Count))
                    all.Add(part);
                _messages[index] = existing with { Parts = all };
                RememberCalls(all, index);
                return;
            }

            var id = NextId(entry);
            var stamped = Stamp(id, parts);
            _messages.Add(new HarnessMessage
            {
                Id = id,
                Role = "assistant",
                Parts = stamped,
                Timestamp = Time(entry) ?? DateTimeOffset.UnixEpoch,
                ModelId = model is "<synthetic>" ? null : model,
                Finish = String(message, "stop_reason"),
            });
            _claudeMessageId = claudeId;
            RememberCalls(stamped, _messages.Count - 1);
        }

        private void AddCompaction(JsonElement entry)
        {
            _claudeMessageId = null;
            var metadata = entry.TryGetProperty("compactMetadata", out var m) && m.ValueKind == JsonValueKind.Object ? m
                : entry.TryGetProperty("compact_metadata", out var snake) && snake.ValueKind == JsonValueKind.Object ? snake
                : default;
            var id = NextId(entry);
            _messages.Add(new HarnessMessage
            {
                Id = id,
                Role = "assistant",
                Parts =
                [
                    new CompactionPart(
                        Trigger: String(metadata, "trigger") switch
                        {
                            "auto" => ContextCompactionTriggers.Auto,
                            "manual" => ContextCompactionTriggers.Manual,
                            _ => null,
                        },
                        TokensBefore: Int(metadata, "preTokens") ?? Int(metadata, "pre_tokens"),
                        TokensAfter: Int(metadata, "postTokens") ?? Int(metadata, "post_tokens"))
                    { PartId = $"{id}-part-0" },
                ],
                Timestamp = Time(entry) ?? DateTimeOffset.UnixEpoch,
            });
        }

        private ToolUsePart ToolCall(JsonElement block)
        {
            var name = String(block, "name") ?? string.Empty;
            var input = block.TryGetProperty("input", out var raw) ? raw.Clone() : default;
            if (String(block, "id") is { } callId)
                _rawCalls[callId] = (name, input);
            return new ToolUsePart(
                ToolCallId: String(block, "id") ?? string.Empty,
                ToolName: ClaudeCodeTools.Name(name),
                Arguments: ClaudeCodeTools.Input(name, input),
                State: ToolUseState.Running)
            {
                Metadata = ClaudeCodeTools.Metadata(input.ValueKind == JsonValueKind.Object ? ClaudeCodeTools.Diff(name, input) : null),
            };
        }

        private void RememberCalls(IReadOnlyList<MessagePart> parts, int message)
        {
            foreach (var part in parts)
            {
                if (part is ToolUsePart tool && tool.ToolCallId.Length > 0)
                    _callMessages[tool.ToolCallId] = message;
            }
        }

        private void FinishCall(JsonElement block, JsonElement entry)
        {
            if (String(block, "tool_use_id") is not { } callId || !_callMessages.TryGetValue(callId, out var messageIndex))
                return;

            var message = _messages[messageIndex];
            var parts = message.Parts.ToList();
            var index = parts.FindIndex(p => p is ToolUsePart t && t.ToolCallId == callId);
            if (index < 0)
                return;

            var content = ResultText(block);
            var isError = Bool(block, "is_error");
            var finished = (ToolUsePart)parts[index] with
            {
                State = isError ? ToolUseState.Error : ToolUseState.Completed,
                Error = isError ? content : null,
            };

            if (!isError && entry.TryGetProperty("toolUseResult", out var result) && _rawCalls.TryGetValue(callId, out var raw)
                && raw.Input.ValueKind == JsonValueKind.Object
                && ClaudeCodeTools.Diff(raw.Name, raw.Input, result.Clone()) is { } diff)
            {
                finished = finished with { Metadata = ClaudeCodeTools.Metadata(diff) };
            }

            parts[index] = finished;
            parts.Add(new ToolResultPart(callId, content, isError));
            _messages[messageIndex] = message with { Parts = parts };
        }

        private static string ResultText(JsonElement block)
        {
            if (!block.TryGetProperty("content", out var content))
                return string.Empty;
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
            if (content.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var text = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (String(part, "type") == "text" && String(part, "text") is { } value)
                {
                    if (text.Length > 0)
                        text.Append('\n');
                    text.Append(value);
                }
            }

            return text.ToString();
        }

        private static FilePart? Image(JsonElement block, int index)
        {
            if (!block.TryGetProperty("source", out var source) || String(source, "type") != "base64")
                return null;
            var mime = String(source, "media_type");
            var data = String(source, "data");
            return mime is null || data is null ? null : new FilePart($"image-{index}", mime, $"data:{mime};base64,{data}", null);
        }

        /// <summary>Gives each part the id the live stream gives it, so history and the conversation name the same part.</summary>
        private static List<MessagePart> Stamp(string messageId, List<MessagePart> parts, int offset = 0)
        {
            var stamped = new List<MessagePart>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                var partId = $"{messageId}-part-{offset + i}";
                stamped.Add(parts[i] switch
                {
                    TextPart text => text with { PartId = partId },
                    ReasoningPart reasoning => reasoning with { PartId = partId },
                    ToolUsePart tool => tool with { PartId = partId },
                    var other => other,
                });
            }

            return stamped;
        }

        /// <summary>
        /// The id for a message starting at <paramref name="entry"/>: ascending in time like every Fleet message id, and
        /// the same for the same entry every time it is read.
        /// </summary>
        private string NextId(JsonElement entry)
        {
            var at = (Time(entry) ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds();
            var timestamp = Math.Max(at, _lastTimestamp);
            var counter = timestamp == _lastTimestamp ? _lastCounter + 1 : 0;
            _lastTimestamp = timestamp;
            _lastCounter = counter;
            return AscendingMessageId.Stable(timestamp, counter, "claude-code-transcript:" + (String(entry, "uuid") ?? string.Empty));
        }
    }

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool Bool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement entry)
        => String(entry, "timestamp") is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;
}
