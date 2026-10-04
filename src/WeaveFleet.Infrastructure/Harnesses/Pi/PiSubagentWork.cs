using System.Globalization;
using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>
/// The subagents of Pi's example <c>subagent</c> extension (<c>examples/extensions/subagent</c>, installed under
/// <c>~/.pi/agent/extensions</c>) as running work. Pi has no subagents of its own: the extension's tool runs each one as
/// a separate <c>pi --mode json --no-session</c> process inside the tool call, and reports them in the call's
/// <c>details.results[]</c> (<c>{agent, task, step, exitCode, model, stopReason, messages}</c>) on every
/// <c>tool_execution_update</c> and on <c>tool_execution_end</c>.
/// </summary>
/// <remarks>
/// One call can run one agent, several side by side (<c>tasks</c>) or a chain (<c>chain</c>), so each result is a piece of
/// work of its own, named <c>{call id}:{index}</c>. None has a session Fleet could open, none can be stopped on its own
/// (interrupting the turn stops the call and every agent in it), and there's no output to read beyond what's reported.
/// A tool with the same name but another shape is left alone: it's only a tool card.
/// </remarks>
internal sealed class PiSubagentWork(string sessionId)
{
    /// <summary>The extension's tool.</summary>
    internal const string ToolName = "subagent";

    private const int MaxSummaryLength = 160;

    private readonly object _sync = new();
    private readonly Dictionary<string, Tracked> _work = new(StringComparer.Ordinal);

    /// <summary>What a <c>tool_execution_update</c> of the tool says about its agents.</summary>
    internal IReadOnlyList<HarnessEvent> Update(string toolCallId, JsonElement? args, PiToolResult? partialResult)
        => Report(toolCallId, args, partialResult?.Details, callEnded: false, callFailed: false, failure: null);

    /// <summary>
    /// What the call's <c>tool_execution_end</c> says: every agent in it is done. One the final result doesn't list (an
    /// interrupted call reports none) ended with the call: cancelled when it was interrupted, otherwise as the call did.
    /// </summary>
    internal IReadOnlyList<HarnessEvent> End(string toolCallId, JsonElement? args, PiToolResult? result, bool isError)
    {
        var failure = string.Concat((result?.Content ?? []).OfType<PiTextContent>().Select(static text => text.Text));
        return Report(toolCallId, args, result?.Details, callEnded: true, callFailed: isError, failure);
    }

    /// <summary>
    /// Pi's run ended (<c>agent_end</c>). A call whose end Pi never reported can't still be running: its agents ended
    /// with it.
    /// </summary>
    internal IReadOnlyList<HarnessEvent> AgentEnded()
    {
        lock (_sync)
        {
            var events = _work.Values
                .Where(static tracked => !tracked.Ended)
                .Select(tracked => WorkEvents.Ended(tracked.Report, WorkEndedReasons.Cancelled, sessionId))
                .ToList();
            _work.Clear();
            return events;
        }
    }

    /// <summary>The agents running now, for <see cref="IHarnessSession.GetRunningWorkAsync"/>.</summary>
    internal IReadOnlyList<WorkReport> Running()
    {
        lock (_sync)
        {
            return _work.Values.Where(static tracked => !tracked.Ended).Select(static tracked => tracked.Report).ToList();
        }
    }

    private List<HarnessEvent> Report(
        string toolCallId,
        JsonElement? args,
        JsonElement? details,
        bool callEnded,
        bool callFailed,
        string? failure)
    {
        var results = Results(details);
        var chainLength = args is { ValueKind: JsonValueKind.Object } a
            && a.TryGetProperty("chain", out var chain) && chain.ValueKind == JsonValueKind.Array
                ? chain.GetArrayLength()
                : 0;

        lock (_sync)
        {
            var events = new List<HarnessEvent>();
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                var report = new WorkReport
                {
                    WorkId = WorkId(toolCallId, index),
                    Kind = WorkKinds.Subagent,
                    Title = result.Agent,
                    Label = result.Task,
                    ToolCallId = toolCallId,
                    CanStop = false,
                    CanReadOutput = false,
                    Detail = Describe(result, chainLength),
                };

                // A chain runs one step at a time: a step with one after it is done.
                var ended = callEnded || (chainLength > 0 && index < results.Count - 1) || result.Finished;
                Track(events, report, ended ? Outcome(result) : null);
            }

            if (!callEnded)
                return events;

            // What the end didn't list ended with the call.
            var reason = !callFailed
                ? WorkEndedReasons.Completed
                : failure?.Contains("aborted", StringComparison.OrdinalIgnoreCase) == true
                    ? WorkEndedReasons.Cancelled
                    : WorkEndedReasons.Error;
            var prefix = toolCallId + ":";
            foreach (var (workId, tracked) in _work.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                if (!tracked.Ended)
                    events.Add(WorkEvents.Ended(tracked.Report, reason, sessionId));
                _work.Remove(workId);
            }

            return events;
        }
    }

    /// <summary>Starts, updates or ends the work as <paramref name="report"/> says, once each.</summary>
    private void Track(List<HarnessEvent> events, WorkReport report, (string Reason, string? Summary)? outcome)
    {
        var known = _work.TryGetValue(report.WorkId, out var tracked);
        if (known && tracked!.Ended)
            return;

        if (!known)
            events.Add(WorkEvents.Started(report, sessionId));
        else if (outcome is null && report.Detail != tracked!.Report.Detail)
            events.Add(WorkEvents.Updated(report, sessionId));

        if (outcome is { } end)
        {
            var detail = end.Summary is null ? report.Detail : Join(report.Detail, end.Summary);
            events.Add(WorkEvents.Ended(report with { Detail = detail }, end.Reason, sessionId));
        }

        _work[report.WorkId] = new Tracked(report, outcome is not null);
    }

    /// <summary>How a finished agent ended, and its result in a line.</summary>
    private static (string Reason, string? Summary) Outcome(SubagentResult result)
    {
        if (result.StopReason == "aborted" || result.ExitCode == -1)
            return (WorkEndedReasons.Cancelled, null);

        if (result.ExitCode != 0 || result.StopReason == "error")
        {
            var why = FirstLine(result.ErrorMessage) ?? FirstLine(result.Stderr);
            return (WorkEndedReasons.Error, why is null ? $"exit {result.ExitCode}" : $"exit {result.ExitCode}: {why}");
        }

        return (WorkEndedReasons.Completed, FirstLine(result.Output));
    }

    /// <summary>Its step in a chain and its model: <c>step 1 of 2 · claude-haiku-4.5</c>.</summary>
    private static string? Describe(SubagentResult result, int chainLength)
    {
        var step = result.Step is { } n && chainLength > 0
            ? string.Create(CultureInfo.InvariantCulture, $"step {n} of {chainLength}")
            : null;

        // The extension names models as Pi's --model takes them (provider/id); the id is what people know.
        var model = result.Model is { Length: > 0 } m ? m[(m.LastIndexOf('/') + 1)..] : null;
        return Join(step, model);
    }

    private static string? Join(string? first, string? second)
        => first is null ? second : second is null ? first : $"{first} · {second}";

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n').Select(static l => l.Trim()).FirstOrDefault(static l => l.Length > 0);
        if (line is null)
            return null;
        return line.Length <= MaxSummaryLength ? line : string.Concat(line.AsSpan(0, MaxSummaryLength - 1), "…");
    }

    private static string WorkId(string toolCallId, int index)
        => string.Create(CultureInfo.InvariantCulture, $"{toolCallId}:{index}");

    /// <summary>The extension's <c>details.results</c>; empty when the details have another shape.</summary>
    private static List<SubagentResult> Results(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } d
            || !d.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<SubagentResult>();
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object
                || String(result, "agent") is not { } agent
                || String(result, "task") is not { } task)
            {
                return [];
            }

            list.Add(new SubagentResult(
                agent,
                task,
                result.TryGetProperty("step", out var step) && step.TryGetInt32(out var s) ? s : null,
                result.TryGetProperty("exitCode", out var exit) && exit.TryGetInt32(out var e) ? e : 0,
                String(result, "model"),
                String(result, "stopReason"),
                String(result, "errorMessage"),
                String(result, "stderr"),
                FinalOutput(result)));
        }

        return list;
    }

    /// <summary>The agent's last words: the first text of its last assistant message.</summary>
    private static string? FinalOutput(JsonElement result)
    {
        if (!result.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        string? output = null;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || String(message, "role") != "assistant"
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var text = content.EnumerateArray()
                .FirstOrDefault(static part => part.ValueKind == JsonValueKind.Object && String(part, "type") == "text");
            if (text.ValueKind == JsonValueKind.Object && String(text, "text") is { } value)
                output = value;
        }

        return output;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record SubagentResult(
        string Agent,
        string Task,
        int? Step,
        int ExitCode,
        string? Model,
        string? StopReason,
        string? ErrorMessage,
        string? Stderr,
        string? Output)
    {
        /// <summary>
        /// Done before its call ended: it failed to start (its exit code is set), or its last turn stopped for good. Its
        /// exit code is 0 while it runs (-1 for one of several that hasn't started), and a turn that stops to call a
        /// tool (<c>toolUse</c>) goes on.
        /// </summary>
        public bool Finished => ExitCode is not (0 or -1) || StopReason is "stop" or "length" or "error" or "aborted";
    }

    private sealed record Tracked(WorkReport Report, bool Ended);
}
