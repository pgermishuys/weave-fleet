using System.Buffers;
using System.Text;
using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// The lines Fleet writes to a <c>claude</c> process's stdin with <c>--input-format stream-json</c>: the prompts, the
/// answers to its <c>can_use_tool</c> asks, and Fleet's own requests (stop the turn, change the model or mode).
/// </summary>
internal static class ClaudeCodeInput
{
    /// <summary>Stops the running turn. Work the agent left running in the background carries on.</summary>
    internal static string Interrupt(string requestId) => Request(requestId, "interrupt", _ => { });

    /// <summary>Runs the following turns on <paramref name="model"/>.</summary>
    internal static string SetModel(string requestId, string model)
        => Request(requestId, "set_model", json => json.WriteString("model", model));

    /// <summary>Runs the following turns at reasoning effort <paramref name="effort"/> (<c>low</c> … <c>max</c>), or the model's default when null.</summary>
    internal static string SetEffort(string requestId, string? effort)
        => Request(requestId, "apply_flag_settings", json =>
        {
            json.WriteStartObject("settings");
            if (effort is null)
                json.WriteNull("effortLevel");
            else
                json.WriteString("effortLevel", effort);
            json.WriteEndObject();
        });

    /// <summary>
    /// Asks what the process offers: its <c>models</c> (with each one's effort levels), commands and agents. Claude Code
    /// answers before any prompt, and lists the models a gateway offers too.
    /// </summary>
    internal static string Initialize(string requestId) => Request(requestId, "initialize", _ => { });

    /// <summary>
    /// Changes the permission mode. Claude Code refuses <c>bypassPermissions</c> unless the process started in it.
    /// </summary>
    internal static string SetPermissionMode(string requestId, string mode)
        => Request(requestId, "set_permission_mode", json => json.WriteString("mode", mode));

    /// <summary>
    /// Stops one task (a background shell, a monitor, a subagent), leaving the turn and the other tasks running. Claude
    /// Code reports its end with <c>task_notification</c> status <c>stopped</c>.
    /// </summary>
    internal static string StopTask(string requestId, string taskId)
        => Request(requestId, "stop_task", json => json.WriteString("task_id", taskId));

    /// <summary>
    /// Asks for the end of a background command's or monitor's output: at most its last 8 KiB, with its whole size.
    /// </summary>
    internal static string GetTaskOutput(string requestId, string taskId)
        => Request(requestId, "get_task_output", json => json.WriteString("task_id", taskId));

    private static string Request(string requestId, string subtype, Action<Utf8JsonWriter> fields) => Line(json =>
    {
        json.WriteString("type", "control_request");
        json.WriteString("request_id", requestId);
        json.WriteStartObject("request");
        json.WriteString("subtype", subtype);
        fields(json);
        json.WriteEndObject();
    });

    /// <summary>The image types Claude takes as <c>image</c> content blocks.</summary>
    internal static readonly IReadOnlySet<string> ImageTypes =
        new HashSet<string>(["image/gif", "image/jpeg", "image/png", "image/webp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The prompt, as a user message. Its <paramref name="images"/> go first, as base64 <c>image</c> blocks (Claude Code
    /// expands a command or a skill only when the text is what the message ends with), then Fleet's
    /// <paramref name="notes"/> to the model (<see cref="PromptOptions.ModelNotes"/>) as text blocks of their own, so the
    /// question is read last; Fleet keeps the prompt without them. Other attachments are left out.
    /// </summary>
    internal static string UserMessage(string text, IReadOnlyList<string>? notes = null, IReadOnlyList<HarnessAttachment>? images = null) => Line(json =>
    {
        var pictures = images?.Where(image => ImageTypes.Contains(image.Mime) && image.Data.Length > 0).ToList() ?? [];
        json.WriteString("type", "user");
        json.WriteStartObject("message");
        json.WriteString("role", "user");
        if (notes is not { Count: > 0 } && pictures.Count == 0)
        {
            json.WriteString("content", text);
        }
        else
        {
            json.WriteStartArray("content");
            foreach (var image in pictures)
            {
                json.WriteStartObject();
                json.WriteString("type", "image");
                json.WriteStartObject("source");
                json.WriteString("type", "base64");
                json.WriteString("media_type", image.Mime.ToLowerInvariant());
                json.WriteString("data", image.Data);
                json.WriteEndObject();
                json.WriteEndObject();
            }

            foreach (var block in (notes ?? []).Append(text))
            {
                json.WriteStartObject();
                json.WriteString("type", "text");
                json.WriteString("text", block);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    });

    /// <summary>Lets the tool call run, with the input it asked with.</summary>
    internal static string Allow(string requestId, JsonElement input) => Response(requestId, json =>
    {
        json.WriteString("behavior", "allow");
        json.WritePropertyName("updatedInput");
        if (input.ValueKind == JsonValueKind.Object)
            input.WriteTo(json);
        else
        {
            json.WriteStartObject();
            json.WriteEndObject();
        }
    });

    /// <summary>
    /// Answers an <c>AskUserQuestion</c> call: it runs with the <paramref name="questions"/> it asked and the user's
    /// <paramref name="answers"/>, by question text, each the chosen labels joined with ", ".
    /// </summary>
    internal static string Answer(string requestId, JsonElement questions, IReadOnlyDictionary<string, string> answers) => Response(requestId, json =>
    {
        json.WriteString("behavior", "allow");
        json.WriteStartObject("updatedInput");
        json.WritePropertyName("questions");
        questions.WriteTo(json);
        json.WriteStartObject("answers");
        foreach (var (question, answer) in answers)
            json.WriteString(question, answer);
        json.WriteEndObject();
        json.WriteEndObject();
    });

    /// <summary>Tells Claude Code Fleet can't answer a request it doesn't know (a hook callback, say).</summary>
    internal static string Error(string requestId, string error) => Line(json =>
    {
        json.WriteString("type", "control_response");
        json.WriteStartObject("response");
        json.WriteString("subtype", "error");
        json.WriteString("request_id", requestId);
        json.WriteString("error", error);
        json.WriteEndObject();
    });

    /// <summary>Refuses the tool call; <paramref name="message"/> is what the agent is told.</summary>
    internal static string Deny(string requestId, string message) => Response(requestId, json =>
    {
        json.WriteString("behavior", "deny");
        json.WriteString("message", message);
    });

    private static string Response(string requestId, Action<Utf8JsonWriter> answer) => Line(json =>
    {
        json.WriteString("type", "control_response");
        json.WriteStartObject("response");
        json.WriteString("subtype", "success");
        json.WriteString("request_id", requestId);
        json.WriteStartObject("response");
        answer(json);
        json.WriteEndObject();
        json.WriteEndObject();
    });

    private static string Line(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Reads Claude Code's <c>can_use_tool</c> asks into what Fleet shows and matches.</summary>
internal static class ClaudeCodePermissions
{
    private const int DetailLinesShown = 80;

    /// <summary>What the call touches, for matching "Don't ask again": the command, the file, the address.</summary>
    internal static IReadOnlyList<string> Patterns(string tool, JsonElement input)
        => Target(tool, input) is { } target ? [target] : [];

    /// <summary>The ask as Fleet shows it; a file in <paramref name="workingDirectory"/> is named relative to it.</summary>
    internal static PermissionAsk ToAsk(string requestId, string fleetSessionId, ClaudeCodeControlRequestBody request, string? workingDirectory = null)
    {
        var tool = ClaudeCodeTools.PermissionName(request.ToolName ?? "tool");
        return new PermissionAsk
        {
            Id = requestId,
            SessionId = fleetSessionId,
            Kind = PermissionKinds.Classify(tool),
            Tool = tool,
            Title = Relative(Target(tool, request.Input), workingDirectory) ?? request.Description,
            Detail = Detail(tool, request.Input),
            Always = Rules(tool, request.PermissionSuggestions),
            CallId = request.ToolUseId,
        };
    }

    private static string? Relative(string? path, string? workingDirectory)
    {
        if (path is null || workingDirectory is null || !Path.IsPathFullyQualified(path))
            return path;
        var relative = Path.GetRelativePath(workingDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) ? path : relative;
    }

    private static string? Target(string tool, JsonElement input)
        => PermissionEvents.String(input, "command")
            ?? PermissionEvents.String(input, "file_path")
            ?? PermissionEvents.String(input, "notebook_path")
            ?? PermissionEvents.String(input, "url")
            ?? PermissionEvents.String(input, "query")
            ?? PermissionEvents.String(input, "path");

    /// <summary>What an edit changes, as diff lines, or what a write writes; null for anything else.</summary>
    private static string? Detail(string tool, JsonElement input)
    {
        if (PermissionEvents.String(input, "old_string") is { } oldText)
        {
            var newText = PermissionEvents.String(input, "new_string") ?? string.Empty;
            return Clip(string.Join('\n', oldText.Split('\n').Select(line => "- " + line).Concat(newText.Split('\n').Select(line => "+ " + line))));
        }

        return string.Equals(tool, "Write", StringComparison.Ordinal) && PermissionEvents.String(input, "content") is { } content
            ? Clip(string.Join('\n', content.Split('\n').Select(line => "+ " + line)))
            : null;
    }

    private static string Clip(string text)
    {
        var lines = text.Split('\n');
        return lines.Length <= DetailLinesShown ? text : string.Join('\n', lines.Take(DetailLinesShown)) + $"\n… {lines.Length - DetailLinesShown} more lines";
    }

    /// <summary>The rules Claude Code suggests for not asking again about this tool, e.g. <c>git push *</c> for Bash.</summary>
    private static List<string> Rules(string tool, JsonElement? suggestions)
    {
        if (suggestions is not { ValueKind: JsonValueKind.Array } list)
            return [];

        var rules = new List<string>();
        foreach (var suggestion in list.EnumerateArray())
        {
            if (PermissionEvents.String(suggestion, "type") != "addRules"
                || !suggestion.TryGetProperty("rules", out var ruleList)
                || ruleList.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var rule in ruleList.EnumerateArray())
            {
                if (PermissionEvents.String(rule, "toolName") == tool && PermissionEvents.String(rule, "ruleContent") is { } content
                    && !content.StartsWith("domain:", StringComparison.Ordinal))
                {
                    rules.Add(content);
                }
            }
        }

        return rules;
    }
}
