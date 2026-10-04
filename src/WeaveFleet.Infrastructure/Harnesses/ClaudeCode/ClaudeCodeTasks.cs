using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// The work a claude process's agent leaves running, read from its tool calls and its <c>task_*</c> system messages, as
/// Fleet's running work (<see cref="WorkReport"/>). Not thread-safe: <see cref="ClaudeCodeHarnessSession"/> calls it
/// with its gate held.
/// </summary>
/// <remarks>
/// What Claude Code 2.1.289 sends (recorded in <c>tests/contracts/claudecode-work-events.json</c>):
/// <list type="bullet">
/// <item><c>task_started</c> for every task: <c>task_type</c> <c>local_bash</c> for a <c>Bash</c> command or a
/// <c>Monitor</c> (only the tool call tells them apart), <c>local_agent</c> for a subagent; <c>is_backgrounded</c> false
/// for one its call waits on. A long foreground <c>Bash</c> command gets a task too, which isn't work Fleet shows unless
/// it moves to the background.</item>
/// <item><c>task_progress</c> while a subagent works: what it's doing now.</item>
/// <item><c>task_notification</c> when a task ends: <c>completed</c>, <c>failed</c> or <c>stopped</c>, a summary
/// (<c>… (exit code 0)</c>, or a subagent's reply) and its output file.</item>
/// <item><c>background_tasks_changed</c>: the whole list of background tasks, just before the <c>task_started</c> or
/// <c>task_notification</c> that changed it. It can't tell a monitor from a shell or name the call, so work starts from
/// <c>task_started</c>; the list keeps the process alive, says what runs, and moves a task into the background.</item>
/// </list>
/// </remarks>
internal sealed partial class ClaudeCodeTasks
{
    /// <summary>The longest label or detail Fleet keeps for a task.</summary>
    internal const int MaxText = 200;

    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskState> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _background = new(StringComparer.Ordinal);

    // Kept after the process ends, so the output of work that ended can still be read.
    private readonly Dictionary<string, string> _outputFiles = new(StringComparer.Ordinal);
    private string? _tasksDirectory;

    /// <summary>A tool call: which tool, what it was asked, and the subagent call it was made in (null: the session's own).</summary>
    internal sealed record Call(string Name, string? ParentCallId, string? Description, string? Command);

    /// <summary>A change to Fleet's running work, reported on the conversation of <see cref="OwnerCallId"/>.</summary>
    /// <param name="Type"><see cref="EventTypes.WorkStarted"/>, <see cref="EventTypes.WorkUpdated"/> or <see cref="EventTypes.WorkEnded"/>.</param>
    /// <param name="OwnerCallId">The subagent call whose conversation started the work; null for the session's own.</param>
    /// <param name="Prompt">On a subagent's start: what it was asked, which opens its child session.</param>
    internal sealed record Change(string Type, WorkReport Report, string? OwnerCallId, string? Prompt = null);

    /// <summary>The background tasks Claude Code says still run, by id, with what each is.</summary>
    internal IReadOnlyDictionary<string, string> Background => _background;

    /// <summary>Whether any background task still runs, which keeps the process from stopping when idle.</summary>
    internal bool HasBackgroundWork => _background.Count > 0;

    /// <summary>The tool call <paramref name="callId"/>, when this process made it.</summary>
    internal Call? FindCall(string callId) => _calls.GetValueOrDefault(callId);

    /// <summary>Remembers a tool call, made in subagent call <paramref name="parentCallId"/>'s conversation or the session's own.</summary>
    internal void ObserveToolUse(string callId, string? name, JsonElement input, string? parentCallId)
    {
        if (string.IsNullOrEmpty(callId))
            return;
        _calls[callId] = new Call(
            name ?? string.Empty,
            parentCallId,
            PermissionEvents.String(input, "description"),
            PermissionEvents.String(input, "command"));
    }

    /// <summary>Learns where task output goes from a tool's result (<c>Output is being written to: …/tasks/b51djw65i.output</c>).</summary>
    internal void ObserveToolResult(string? content)
    {
        if (string.IsNullOrEmpty(content) || OutputFilePattern().Match(content) is not { Success: true } match)
            return;
        RememberOutputFile(Path.GetFileNameWithoutExtension(match.Groups["path"].Value), match.Groups["path"].Value);
    }

    /// <summary>What a <c>task_*</c> or <c>background_tasks_changed</c> message changes in Fleet's running work.</summary>
    internal IReadOnlyList<Change> Observe(ClaudeCodeSystemMessage system) => system.Subtype switch
    {
        "task_started" when system.TaskId is { Length: > 0 } => Started(system),
        "task_progress" when system.TaskId is { Length: > 0 } id => Progress(id, system.Description),
        "task_notification" when system.TaskId is { Length: > 0 } id => Notified(id, system),
        "background_tasks_changed" when system.Tasks is { } tasks => BackgroundChanged(tasks),
        _ => [],
    };

    /// <summary>
    /// The process ended: what was still running went with it and won't report back, so it ends
    /// <see cref="WorkEndedReasons.Lost"/>. The output files are kept.
    /// </summary>
    internal IReadOnlyList<Change> EndAll()
    {
        var lost = _tasks.Values
            .Where(task => task.Reported && task.Running)
            .Select(task => new Change(EventTypes.WorkEnded, new WorkReport { WorkId = task.Id, EndedReason = WorkEndedReasons.Lost }, task.OwnerCallId))
            .ToList();
        _tasks.Clear();
        _background.Clear();
        _calls.Clear();
        return lost;
    }

    /// <summary>The work Fleet was told of that still runs, as the harness's own list (<see cref="IHarnessSession.GetRunningWorkAsync"/>).</summary>
    internal IReadOnlyList<WorkReport> Running()
        => [.. _tasks.Values.Where(task => task.Reported && task.Running).Select(task => new WorkReport { WorkId = task.Id, Kind = task.Kind })];

    /// <summary>Whether Fleet was told of task <paramref name="taskId"/> and it still runs.</summary>
    internal bool IsRunning(string taskId) => _tasks.TryGetValue(taskId, out var task) && task.Reported && task.Running;

    /// <summary>The file task <paramref name="taskId"/>'s output goes to, when this session has seen where.</summary>
    internal string? OutputFile(string taskId)
        => _outputFiles.GetValueOrDefault(taskId)
            ?? (_tasksDirectory is not null && IsTaskId(taskId) ? Path.Combine(_tasksDirectory, taskId + ".output") : null);

    private IReadOnlyList<Change> Started(ClaudeCodeSystemMessage system)
    {
        var id = system.TaskId!;
        if (_tasks.TryGetValue(id, out var known))
        {
            // A background subagent woken by its own background work starts again under the same id. Fleet already
            // ended it, and work that ended stays ended, so this run isn't reported; its child session shows it.
            if (!known.Running)
                known.Reported = false;
            known.Running = true;
            if (system.IsBackgrounded == true)
                _background.TryAdd(id, known.Label);
            return [];
        }

        var call = system.ToolUseId is { } callId ? _calls.GetValueOrDefault(callId) : null;
        var kind = KindOf(system.TaskType, system.SubagentType, call);
        var background = system.IsBackgrounded == true;
        var task = new TaskState
        {
            Id = id,
            Kind = kind,
            ToolCallId = system.ToolUseId,
            // A subagent's work is its caller's: a nested subagent's belongs to the subagent that started it. Shells and
            // monitors are the session's, which owns the process that runs them and can stop them or read their output.
            OwnerCallId = kind == WorkKinds.Subagent ? call?.ParentCallId : null,
            Title = TitleOf(kind, system, call),
            Label = Clip(LabelOf(kind, system, call)),
            Background = background,
            Running = true,
        };
        _tasks[id] = task;
        if (background)
            _background[id] = task.Label;

        // Every subagent is work, with a child session of its own; a command only once it runs in the background.
        return kind == WorkKinds.Subagent || background ? [Report(task, EventTypes.WorkStarted) with { Prompt = system.Prompt }] : [];
    }

    private IReadOnlyList<Change> Progress(string id, string? description)
    {
        if (!_tasks.TryGetValue(id, out var task) || !task.Reported || !task.Running || string.IsNullOrWhiteSpace(description))
            return [];

        var detail = Clip(description);
        if (detail == task.Detail)
            return [];
        task.Detail = detail;
        return [new Change(EventTypes.WorkUpdated, new WorkReport { WorkId = id, Detail = detail }, task.OwnerCallId)];
    }

    private IReadOnlyList<Change> Notified(string id, ClaudeCodeSystemMessage system)
    {
        if (system.OutputFile is { Length: > 0 } outputFile)
            RememberOutputFile(id, outputFile);
        _background.Remove(id);

        if (!_tasks.TryGetValue(id, out var task) || !task.Running)
            return [];
        task.Running = false;
        if (!task.Reported)
            return [];

        var reason = system.Status switch
        {
            "failed" or "error" => WorkEndedReasons.Error,
            "stopped" or "killed" or "cancelled" => WorkEndedReasons.Cancelled,
            _ => WorkEndedReasons.Completed,
        };
        var report = new WorkReport { WorkId = id, EndedReason = reason, Detail = DetailOf(task.Kind, reason, system.Summary) };
        return [new Change(EventTypes.WorkEnded, report, task.OwnerCallId)];
    }

    private List<Change> BackgroundChanged(IReadOnlyList<ClaudeCodeBackgroundTask> tasks)
    {
        _background.Clear();
        var changes = new List<Change>();
        foreach (var entry in tasks)
        {
            if (entry.TaskId is not { Length: > 0 } id)
                continue;
            _background[id] = entry.Description ?? id;

            // A task its call was waiting on went to the background (it ran past Bash's time limit, say).
            if (!_tasks.TryGetValue(id, out var task) || !task.Running || task.Background)
                continue;
            task.Background = true;
            changes.Add(task.Reported
                ? new Change(EventTypes.WorkUpdated, new WorkReport { WorkId = id, Background = true, CanStop = true }, task.OwnerCallId)
                : Report(task, EventTypes.WorkStarted));
        }

        return changes;
    }

    private static Change Report(TaskState task, string type)
    {
        task.Reported = true;
        return new Change(type, new WorkReport
        {
            WorkId = task.Id,
            Kind = task.Kind,
            Title = task.Title,
            Label = task.Label,
            ToolCallId = task.ToolCallId,
            Background = task.Background,
            // A subagent's child session is named after its call, which every line of its conversation carries.
            ChildHarnessSessionId = task.Kind == WorkKinds.Subagent ? task.ToolCallId : null,
            // stop_task stops one task and leaves the turn running (checked against the real CLI for background
            // shells and subagents); a task the turn waits on is stopped with the turn.
            CanStop = task.Background,
            CanReadOutput = task.Kind is WorkKinds.Shell or WorkKinds.Monitor,
        }, task.OwnerCallId);
    }

    private void RememberOutputFile(string taskId, string path)
    {
        if (!IsTaskId(taskId) || !Path.IsPathFullyQualified(path))
            return;
        _outputFiles[taskId] = path;
        _tasksDirectory ??= Path.GetDirectoryName(path);
    }

    private static string KindOf(string? taskType, string? subagentType, Call? call) => taskType switch
    {
        "local_bash" => call?.Name == "Monitor" ? WorkKinds.Monitor : WorkKinds.Shell,
        "local_agent" or "remote_agent" => WorkKinds.Subagent,
        _ when subagentType is not null || call?.Name is "Agent" or "Task" => WorkKinds.Subagent,
        _ => WorkKinds.Task,
    };

    private static string TitleOf(string kind, ClaudeCodeSystemMessage system, Call? call) => kind switch
    {
        WorkKinds.Subagent => system.SubagentType ?? "subagent",
        WorkKinds.Shell or WorkKinds.Monitor => call?.Name is { Length: > 0 } tool ? tool : kind,
        _ => system.TaskType ?? WorkKinds.Task,
    };

    private static string LabelOf(string kind, ClaudeCodeSystemMessage system, Call? call) => kind switch
    {
        // What the command is, rather than the model's description of it.
        WorkKinds.Shell or WorkKinds.Monitor => call?.Command ?? system.Description ?? system.TaskId!,
        _ => system.Description ?? call?.Description ?? system.TaskId!,
    };

    /// <summary>How a task ended, in a few words: <c>exit 0</c> for a command, the start of a subagent's reply.</summary>
    private static string? DetailOf(string kind, string reason, string? summary)
    {
        if (reason == WorkEndedReasons.Cancelled)
            return "stopped";
        if (kind is WorkKinds.Shell or WorkKinds.Monitor)
            return summary is not null && ExitCodePattern().Match(summary) is { Success: true } exit ? $"exit {exit.Groups["code"].Value}" : null;
        return string.IsNullOrWhiteSpace(summary) ? null : Clip(summary);
    }

    /// <summary>One line, at most <see cref="MaxText"/> characters.</summary>
    internal static string Clip(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxText ? line : line[..(MaxText - 1)].TrimEnd() + "…";
    }

    // A task id is a file name in Claude Code's tasks folder; nothing else is read from there.
    private static bool IsTaskId(string id) => id.Length is > 0 and <= 64 && TaskIdPattern().IsMatch(id);

    [GeneratedRegex(@"Output is being written to: (?<path>\S+?\.output)")]
    private static partial Regex OutputFilePattern();

    [GeneratedRegex(@"\(exit code (?<code>-?\d+)\)")]
    private static partial Regex ExitCodePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TaskIdPattern();

    private sealed class TaskState
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public string? ToolCallId { get; init; }
        public string? OwnerCallId { get; init; }
        public required string Title { get; init; }
        public required string Label { get; init; }
        public bool Background { get; set; }
        public bool Running { get; set; }

        /// <summary>Fleet was told of it: every subagent, and commands once they run in the background.</summary>
        public bool Reported { get; set; }

        public string? Detail { get; set; }
    }
}

/// <summary>Reads a page of the file Claude Code writes a background command's or monitor's output to.</summary>
internal static class ClaudeCodeTaskOutput
{
    /// <summary>The most Fleet reads in one page.</summary>
    internal const int PageBytes = 64 * 1024;

    /// <summary>
    /// The text from byte <paramref name="offset"/>, up to <see cref="PageBytes"/>, ending on a whole character. A file
    /// that isn't there (yet, or any more) reads as empty.
    /// </summary>
    internal static WorkOutput Read(string path, long offset)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return new WorkOutput(string.Empty, offset, 0, Truncated: false);
        }

        using (stream)
        {
            var size = stream.Length;
            if (offset >= size)
                return new WorkOutput(string.Empty, size, size, Truncated: false);

            stream.Position = offset;
            var buffer = new byte[(int)Math.Min(PageBytes, size - offset)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var whole = offset + read < size ? WholeCharacters(buffer.AsSpan(0, read)) : read;
            return new WorkOutput(System.Text.Encoding.UTF8.GetString(buffer, 0, whole), offset + whole, size, Truncated: false);
        }
    }

    /// <summary>
    /// A page from <c>get_task_output</c>'s answer (<c>{ output, total_bytes, truncated }</c>): the end of the output, so
    /// what comes before it is left out when <paramref name="offset"/> is earlier.
    /// </summary>
    internal static WorkOutput? FromTail(JsonElement tail, long offset)
    {
        if (tail.ValueKind != JsonValueKind.Object
            || !tail.TryGetProperty("total_bytes", out var totalElement)
            || !totalElement.TryGetInt64(out var size))
        {
            return null;
        }

        if (offset >= size)
            return new WorkOutput(string.Empty, size, size, Truncated: false);

        var bytes = System.Text.Encoding.UTF8.GetBytes(PermissionEvents.String(tail, "output") ?? string.Empty);
        var tailStart = Math.Max(0, size - bytes.Length);
        var skip = (int)Math.Max(0, offset - tailStart);
        return new WorkOutput(System.Text.Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip), size, size, Truncated: offset < tailStart);
    }

    /// <summary>How many of <paramref name="bytes"/> make whole UTF-8 characters: a page doesn't end inside one.</summary>
    private static int WholeCharacters(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
                continue; // a continuation byte: look further back for its lead
            var length = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return length > back ? bytes.Length - back : bytes.Length;
        }

        return bytes.Length;
    }
}
