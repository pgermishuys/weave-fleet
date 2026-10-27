using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>A session as the condenser reads it. Newest first in a list.</summary>
public sealed record TranscriptSession(string Id, string Title, string HarnessType, IReadOnlyList<MessageLifecyclePayload> Messages);

/// <summary>The condensed text, and the sessions in it in label order (<c>S1</c> is the first).</summary>
public sealed record CondensedTranscripts(string Text, IReadOnlyList<TranscriptSession> Used);

/// <summary>
/// Turns sessions' messages, whatever harness wrote them, into the short plain text a dreaming session reads: what the
/// user asked, what failed, what was run again, what notes were saved and what the agent said last each turn.
/// </summary>
public static partial class DreamTranscript
{
    private const int PromptChars = 600;
    private const int OutputChars = 400;
    private const int AgentChars = 200;
    private const int ArgsChars = 160;

    [GeneratedRegex(@"(?:exit(?:ed)?(?:\s+(?:with\s+)?(?:code|status))?|exit_code|exitcode)\W{0,3}(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExitCode();

    [GeneratedRegex(@"command not found|No such file or directory|Permission denied|Traceback \(most recent|^\s*(?:error|fatal)\b[: ]|\bERR!", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ErrorText();

    /// <summary>
    /// Condenses <paramref name="sessions"/> (newest first): each is capped at <see cref="DreamRules.MaxSessionChars"/>, the
    /// set at <see cref="DreamRules.MaxTotalChars"/> by dropping the oldest sessions. A session with nothing in it is left out.
    /// </summary>
    public static CondensedTranscripts Build(IReadOnlyList<TranscriptSession> sessions)
    {
        var bodies = sessions
            .Select(session => (Session: session, Body: Condense(session.Messages)))
            .Where(entry => entry.Body.Length > 0)
            .ToList();

        while (bodies.Count > 1 && bodies.Sum(entry => entry.Body.Length + entry.Session.Title.Length + 40) > DreamRules.MaxTotalChars)
            bodies.RemoveAt(bodies.Count - 1);

        var text = new StringBuilder();
        for (var index = 0; index < bodies.Count; index++)
        {
            var (session, body) = bodies[index];
            text.Append("## S").Append(index + 1).Append(" · ").Append(OneLine(session.Title)).Append(" · ").Append(session.HarnessType).Append('\n');
            text.Append(body).Append('\n');
        }

        return new CondensedTranscripts(text.ToString().TrimEnd(), [.. bodies.Select(entry => entry.Session)]);
    }

    /// <summary>One session's lines, at most <see cref="DreamRules.MaxSessionChars"/> characters.</summary>
    public static string Condense(IReadOnlyList<MessageLifecyclePayload> messages)
    {
        var lines = new List<string>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        string? lastAgent = null;

        void EndTurn()
        {
            if (lastAgent is not null)
                lines.Add($"Agent: {Cut(lastAgent, AgentChars)}");
            lastAgent = null;
        }

        foreach (var message in messages)
        {
            if (message.Info.Role == "user")
            {
                EndTurn();
                var prompt = OneLine(string.Join(' ', message.Parts.OfType<TextMessageEventPart>().Select(part => part.Text)));
                if (prompt.Length > 0)
                    lines.Add($"You: {Cut(prompt, PromptChars)}");
                continue;
            }

            if (message.Info.Role != "assistant")
                continue;

            foreach (var part in message.Parts)
            {
                if (part is TextMessageEventPart text && OneLine(text.Text) is { Length: > 0 } said)
                    lastAgent = said;
                else if (part is ToolMessageEventPart tool)
                    ToolLines(tool, lines, failed);
            }
        }

        EndTurn();

        var body = new StringBuilder();
        foreach (var line in lines)
        {
            if (body.Length + line.Length + 1 > DreamRules.MaxSessionChars)
            {
                body.Append("…\n");
                break;
            }

            body.Append(line).Append('\n');
        }

        return body.ToString().TrimEnd();
    }

    private static void ToolLines(ToolMessageEventPart tool, List<string> lines, HashSet<string> failed)
    {
        var (input, output, error, metadata, done) = Read(tool.State);
        if (!done)
            return;

        var name = tool.ToolName;
        if (MemoryToolKind(name) is { } memory)
        {
            if (memory == "save")
                lines.Add($"Saved note: {Cut(Property(input, "text") ?? Short(input), 200)}");
            else
                lines.Add($"Forgot note {Property(input, "id") ?? Short(input)}");
            return;
        }

        var shell = IsShell(name);
        var command = shell ? OneLine(Property(input, "command") ?? Property(input, "cmd") ?? Short(input)) : null;
        var outputText = Text(output);
        var failure = error ?? (shell ? ShellFailure(outputText, metadata) : null);

        if (shell && command!.Length > 0 && failed.Contains(command))
            lines.Add($"Ran again: {Cut(command, ArgsChars)}");

        if (failure is null && !(tool.State is ToolErrorState))
            return;

        var what = shell ? command! : Short(input);
        lines.Add($"Failed {name}: {Cut(what, ArgsChars)}");
        var detail = failure ?? outputText;
        if (!string.IsNullOrWhiteSpace(detail))
            lines.Add($"  → {Tail(OneLine(detail), OutputChars)}");
        if (shell && command!.Length > 0)
            failed.Add(command);
    }

    private static (JsonElement? Input, JsonElement? Output, string? Error, JsonElement? Metadata, bool Done) Read(ToolInvocationState state) => state switch
    {
        ToolErrorState e => (e.Input, e.Output, string.IsNullOrWhiteSpace(e.Error) ? "The call failed." : e.Error, e.Metadata, true),
        ToolCompletedState c => (c.Input, c.Output, null, c.Metadata, true),
        _ => (null, null, null, null, false),
    };

    /// <summary>"save" or "forget" for Fleet's memory tools under any harness's spelling (<c>mcp__fleet__fleet_memory_save</c>).</summary>
    public static string? MemoryToolKind(string tool)
        => tool.EndsWith("fleet_memory_save", StringComparison.OrdinalIgnoreCase) ? "save"
            : tool.EndsWith("fleet_memory_forget", StringComparison.OrdinalIgnoreCase) ? "forget"
            : null;

    private static bool IsShell(string tool)
    {
        var name = tool.ToLowerInvariant();
        return name is "bash" or "shell" or "sh" or "exec" or "run_command" || name.Contains("bash") || name.Contains("shell");
    }

    private static string? ShellFailure(string output, JsonElement? metadata)
    {
        foreach (var key in new[] { "exit", "exitCode", "exit_code" })
        {
            if (metadata is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty(key, out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value != 0)
                return output.Length > 0 ? output : $"exit code {value}";
        }

        if (ExitCode().Match(output) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var exit) && exit != 0)
            return output;

        return ErrorText().IsMatch(output) ? output : null;
    }

    private static string? Property(JsonElement? input, string name)
        => input is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A call's arguments in a few words: its first text value, or the JSON itself.</summary>
    private static string Short(JsonElement? input)
    {
        if (input is not { } element)
            return "";
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } value)
                    return OneLine(value);
            }
        }

        return OneLine(element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText());
    }

    private static string Text(JsonElement? value)
        => value is not { } element ? "" : element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : element.GetRawText();

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cut(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    private static string Tail(string text, int max) => text.Length <= max ? text : string.Concat("…", text.AsSpan(text.Length - max + 1));
}
