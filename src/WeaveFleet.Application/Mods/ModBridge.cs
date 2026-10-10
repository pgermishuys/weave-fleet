using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// The mod tools (<c>fleet_mod_write</c>, <c>_check</c>, <c>_reload</c>, <c>_test</c>, <c>_keep</c>, <c>_list</c>), for
/// calls from a harness process. The session is the one the call resolves to through
/// <see cref="IHarnessCanvasCallerResolver"/>, as for the memory tools; drafts are that session's, in its owner's folder.
/// Every call checks the Mods switch, safe mode and the mod runtime again, in that order: a process started while Mods
/// were on keeps its tools until it's recycled.
/// </summary>
public sealed class ModBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ModsFeature feature,
    ModsSafeMode safeMode,
    ModService mods,
    IModDraftRunner runner,
    ModKeepRequests keepRequests)
{
    /// <summary>How long <c>fleet_mod_keep</c> waits for the user's answer before it hands back.</summary>
    public const int KeepWaitSeconds = 60;

    /// <summary>The longest test result shown, in characters.</summary>
    public const int ResultLimit = 16_000;

    private const int LogLines = 20;

    public const string TurnedOffMessage = "Mods are turned off in Fleet's Settings, so the mod tools can't run. Ask the user to turn on Mods.";
    public const string SafeModeRefusal = "Fleet was started without mods, so drafts don't load. The user can turn mods back on from the banner.";
    public const string SafeModeNote = "Fleet was started without mods: the draft won't load until the user turns mods back on.";

    private static readonly string[] Events = ["ui.render", "session.start", "turn.complete", "ui.press", "ui.input", "ui.select"];
    private static readonly string[] Components = ["ToolUse", "ToolResult", "ComposerBand", "StatusChip", "Pane"];

    private static long _testRequests;

    private enum Tool
    {
        Write,
        Check,
        Reload,
        Test,
        Keep,
        List,
    }

    public Task<CanvasResult<CanvasToolOutput>> WriteAsync(
        string? bridgeToken, string? harnessSessionId, string? name, IReadOnlyList<ModFile>? files, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, Tool.Write, async gate =>
        {
            if (files is not { Count: > 0 } || files.Any(f => string.IsNullOrWhiteSpace(f.Path)))
                return Fail(CanvasErrorKind.Invalid, "Send the files as a list of {path, content}, at least one, each with a path.");

            var written = await mods.WriteDraftAsync(gate.SessionId, name!, files, ct).ConfigureAwait(false);
            if (written.IsFailure)
                return Fail(CanvasErrorKind.Invalid, written.Error.Description);

            var output = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"Wrote {files.Count} file(s) to the draft {name}: {string.Join(", ", files.Select(f => f.Path))}.")
                .Append("\n\n");

            if (gate.NotReady is { } reason)
            {
                output.Append(CultureInfo.InvariantCulture, $"The check didn't run: the mod runtime isn't ready yet ({reason}).");
            }
            else
            {
                var check = await mods.CheckDraftAsync(gate.SessionId, name!, ct).ConfigureAwait(false);
                if (check.IsFailure)
                {
                    output.Append(CultureInfo.InvariantCulture, $"The check didn't run: {check.Error.Description}");
                }
                else if (check.Value.Check is { } report)
                {
                    output.Append(ModCheckText.Format(report));
                    if (IsNotOk(report))
                        output.Append("\nThe draft won't load until these errors are fixed.");
                }
                else
                {
                    output.Append("The check didn't run: no checker is available.");
                }
            }

            return Ok($"Wrote {name}", WithSafeModeNote(output, gate));
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> CheckAsync(string? bridgeToken, string? harnessSessionId, string? name, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, Tool.Check, async gate =>
        {
            var check = await mods.CheckDraftAsync(gate.SessionId, name!, ct).ConfigureAwait(false);
            if (check.IsFailure)
                return Fail(CanvasErrorKind.Invalid, check.Error.Description);

            var output = new StringBuilder(check.Value.Check is { } report ? ModCheckText.Format(report) : "No checker is available, so there is no check report.");
            if (runner.LoadProblem(gate.UserId, gate.SessionId, name!) is { } problem)
                output.Append(CultureInfo.InvariantCulture, $"\nLast load: {problem.Message}");

            AppendLog(output, runner.Log(gate.UserId, name!, gate.SessionId).TakeLast(LogLines).ToList());
            return Ok($"Checked {name}", WithSafeModeNote(output, gate));
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> ReloadAsync(string? bridgeToken, string? harnessSessionId, string? name, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, Tool.Reload, async gate =>
        {
            var wasOff = gate.Draft!.Off;
            if (wasOff is not null)
            {
                var on = await mods.SetDraftOnAsync(gate.SessionId, name!, on: true, ct).ConfigureAwait(false);
                if (on.IsFailure)
                    return Fail(CanvasErrorKind.Invalid, on.Error.Description);
            }

            var load = await runner.ReloadAsync(gate.UserId, gate.SessionId, name!, ct).ConfigureAwait(false);
            if (!load.Loaded)
            {
                var refused = new StringBuilder($"{name} didn't load: {load.Message ?? "no reason was given."}");
                if (load.Report is { ValueKind: JsonValueKind.Object } report)
                    refused.Append('\n').Append(ModCheckText.Format(report));
                return Ok($"{name} didn't load", refused.ToString());
            }

            var output = new StringBuilder($"{name} loaded in this session.\n");
            var hooks = FormatHooks(load.Hooks);
            if (hooks.Count == 0)
            {
                output.Append("Hooks: (none)");
            }
            else
            {
                output.Append("Hooks:");
                foreach (var hook in hooks)
                    output.Append("\n  ").Append(hook);
            }

            if (wasOff is not null)
            {
                output.Append(wasOff.By == ModOffBy.Strikes
                    ? "\nIt was off after three failures; it's on again."
                    : "\nIt was turned off; it's on again.");
            }

            return Ok($"Reloaded {name}", output.ToString());
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> TestAsync(
        string? bridgeToken, string? harnessSessionId, string? name, string? eventName, JsonElement e, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, Tool.Test, async gate =>
        {
            if (eventName is null || !Events.Contains(eventName))
                return Fail(CanvasErrorKind.Invalid, $"{eventName} isn't an event to test: use {string.Join(", ", Events)}.");
            if (e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                return Fail(CanvasErrorKind.Invalid, "e must be an object with the event's fields.");

            var built = BuildEvent(eventName, e, gate.SessionId);
            if (built.Error is { } error)
                return Fail(CanvasErrorKind.Invalid, error);

            var before = runner.Log(gate.UserId, name!, gate.SessionId).Count;
            var run = await runner.DispatchAsync(gate.UserId, gate.SessionId, eventName, built.Event, ct).ConfigureAwait(false);
            if (!run.Dispatched)
            {
                var matched = built.Component is { } component ? $"{eventName} {component}" : eventName;
                return Ok(
                    $"Nothing matched {eventName}",
                    $"No hook in this session's mods matched {matched}. Check the matcher: props.tool is Fleet's name for the tool "
                    + "(bash, read, edit…), and the draft must have loaded (fleet_mod_reload).");
            }

            var output = new StringBuilder();
            if (eventName == "ui.render")
                output.Append("Drawn by: ").AppendLine(run.DrawnBy.Count == 0 ? "(none)" : string.Join(", ", run.DrawnBy));

            if (run.Result is { } result)
            {
                var text = Indented(result);
                output.AppendLine("Result:").Append(text.Length > ResultLimit ? text[..ResultLimit] + "\n… (cut)" : text).AppendLine();
            }
            else
            {
                output.AppendLine("Result: (none)");
            }

            if (run.Failures.Count > 0)
            {
                output.AppendLine("Failures:");
                foreach (var failure in run.Failures)
                    output.AppendLine(CultureInfo.InvariantCulture, $"{failure.Mod} {failure.Kind}: {failure.Message} ({failure.Strikes} in a row)");
            }

            // The lines this dispatch wrote; when the log has rolled over and the count can't say, the latest ones.
            var log = runner.Log(gate.UserId, name!, gate.SessionId);
            var added = log.Count >= before ? log.Skip(before).ToList() : log.ToList();
            AppendLog(output, added.TakeLast(LogLines).ToList(), omitWhenEmpty: true);
            return Ok($"Tested {name}: {eventName}", output.ToString().TrimEnd());
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> KeepAsync(
        string? bridgeToken, string? harnessSessionId, string? name, string? note, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, Tool.Keep, async gate =>
        {
            var check = await mods.CheckDraftAsync(gate.SessionId, name!, ct).ConfigureAwait(false);
            if (check.IsFailure)
                return Fail(CanvasErrorKind.Invalid, check.Error.Description);
            if (check.Value.Check is { } report && IsNotOk(report))
                return Ok($"{name} can't be kept yet", $"The check found problems; fix them first.\n{ModCheckText.Format(report)}");

            var asked = await mods.RequestKeepAsync(gate.SessionId, name!, note, ct).ConfigureAwait(false);
            if (asked.IsFailure)
                return Fail(CanvasErrorKind.Invalid, asked.Error.Description);

            // No lock is held here: the user answers from the draft card, which ends the request.
            var decision = await keepRequests.WaitAsync(asked.Value, TimeSpan.FromSeconds(KeepWaitSeconds), ct).ConfigureAwait(false);
            return decision?.Outcome switch
            {
                ModKeepOutcome.Kept => Ok($"Kept {name}", $"The user kept {name} as v{decision.Version}. It runs in all their sessions now."),
                ModKeepOutcome.Declined => Ok($"{name} not kept", $"The user didn't keep {name}. The draft stays in this session."),
                _ => Ok(
                    $"Asked to keep {name}",
                    "Waiting for the user: the request is on the draft card in the conversation. Don't ask again; they'll keep it or not from there."),
            };
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> ListAsync(string? bridgeToken, string? harnessSessionId, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, null, Tool.List, async gate =>
        {
            var drafts = await mods.ListDraftsAsync(gate.SessionId, ct).ConfigureAwait(false);
            var kept = await mods.ListAsync(ct).ConfigureAwait(false);

            var output = new StringBuilder("Drafts in this session:");
            if (drafts.IsFailure || drafts.Value.Count == 0)
            {
                output.Append("\n(none)");
            }
            else
            {
                foreach (var draft in drafts.Value)
                {
                    var power = draft.Off is { } off ? $"off: {OffReason(off)}" : "on";
                    var state = draft.Problem is { } problem ? $"didn't load: {problem.Message}" : draft.Off is not null ? "not loaded" : "loaded";
                    output.Append(CultureInfo.InvariantCulture, $"\n{draft.Name} {draft.Version ?? "(no version)"} · {power} · {state}");
                }
            }

            output.Append("\nKept:");
            if (kept.Mods.Count == 0)
            {
                output.Append("\n(none)");
            }
            else
            {
                foreach (var mod in kept.Mods)
                {
                    var active = mod.Active is { } number ? $"v{number} ({mod.ActiveVersion})" : "(no active version)";
                    output.Append(CultureInfo.InvariantCulture, $"\n{mod.Name} {active} · {(mod.Off is null ? "on" : "off")}");
                }
            }

            return Ok("Mods", WithSafeModeNote(output, gate));
        }, ct);

    // ── The checks every call makes ─────────────────────────────────────

    private sealed record Gate(string UserId, string SessionId, ModDraftView? Draft, bool SafeMode, string? NotReady);

    private async Task<CanvasResult<CanvasToolOutput>> RunAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? name,
        Tool tool,
        Func<Gate, Task<CanvasResult<CanvasToolOutput>>> run,
        CancellationToken ct)
    {
        var caller = string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId)
            ? null
            : await callers.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false);
        if (caller is null)
            return Fail(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);

        using (userScope.Begin(caller.UserId))
        {
            if (!await feature.IsSwitchedOnAsync().ConfigureAwait(false))
                return Fail(CanvasErrorKind.Refused, TurnedOffMessage);

            var inSafeMode = safeMode.IsOn(caller.UserId);
            if (inSafeMode && tool is Tool.Reload or Tool.Test or Tool.Keep)
                return Fail(CanvasErrorKind.Refused, SafeModeRefusal);

            var notReady = runner.NotReadyReason(caller.UserId);
            if (notReady is not null && tool is Tool.Check or Tool.Reload or Tool.Test or Tool.Keep)
                return Fail(CanvasErrorKind.Refused, $"The mod runtime isn't ready yet: {notReady}");

            ModDraftView? draft = null;
            if (tool != Tool.List)
            {
                if (!ModNames.IsValid(name))
                {
                    return Fail(
                        CanvasErrorKind.Invalid,
                        $"{name} isn't a mod name: use lowercase letters, digits and -, starting with a letter, 64 at most, not starting with fleet-.");
                }

                if (tool != Tool.Write)
                {
                    var drafts = await mods.ListDraftsAsync(caller.FleetSessionId, ct).ConfigureAwait(false);
                    draft = drafts.IsSuccess ? drafts.Value.FirstOrDefault(d => d.Name == name) : null;
                    if (draft is null)
                        return Fail(CanvasErrorKind.NotFound, $"There's no draft {name} in this session. Write it with fleet_mod_write first.");
                }
            }

            return await run(new Gate(caller.UserId, caller.FleetSessionId, draft, inSafeMode, notReady)).ConfigureAwait(false);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static CanvasResult<CanvasToolOutput> Ok(string title, string output) => CanvasResult.Ok(new CanvasToolOutput(title, output));

    private static CanvasResult<CanvasToolOutput> Fail(CanvasErrorKind kind, string message) => CanvasResult.Fail<CanvasToolOutput>(kind, message);

    private static string WithSafeModeNote(StringBuilder output, Gate gate)
        => gate.SafeMode ? output.Append('\n').Append(SafeModeNote).ToString() : output.ToString();

    private static bool IsNotOk(JsonElement report)
        => report is { ValueKind: JsonValueKind.Object } && report.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;

    private static string OffReason(ModOff off)
        => off.By == ModOffBy.Strikes ? "turned off after three failures in a row" : "turned off by the user";

    private static void AppendLog(StringBuilder output, List<ModDraftLogLine> lines, bool omitWhenEmpty = false)
    {
        if (lines.Count == 0)
        {
            if (!omitWhenEmpty)
                output.Append("\nLog: (empty)");
            return;
        }

        output.Append("\nLog:");
        foreach (var line in lines)
            output.Append(CultureInfo.InvariantCulture, $"\n{line.At.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {line.Level} {line.Text}");
    }

    /// <summary>What <c>register</c> registered, one hook a line: strings as they are, <c>{event, matcher}</c> objects as the check shows them.</summary>
    private static List<string> FormatHooks(JsonElement hooks)
    {
        if (hooks.ValueKind != JsonValueKind.Array)
            return hooks.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? [] : [hooks.GetRawText()];

        var lines = new List<string>();
        foreach (var hook in hooks.EnumerateArray())
        {
            if (hook.ValueKind == JsonValueKind.String)
            {
                lines.Add(hook.GetString()!);
            }
            else if (hook.ValueKind == JsonValueKind.Object && hook.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var matcher = hook.TryGetProperty("matcher", out var m) && m.ValueKind != JsonValueKind.Null ? $" {m.GetRawText()}" : "";
                lines.Add($"{name.GetString()}{matcher}");
            }
            else
            {
                lines.Add(hook.GetRawText());
            }
        }

        return lines;
    }

    private static string Indented(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            element.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record BuiltEvent(JsonElement Event, string? Component, string? Error);

    /// <summary>The event Fleet sends: the agent's fields, this session's id, and for <c>ui.render</c> the fields a real render has.</summary>
    private static BuiltEvent BuildEvent(string eventName, JsonElement e, string sessionId)
    {
        var fields = e.ValueKind == JsonValueKind.Object ? JsonNode.Parse(e.GetRawText())!.AsObject() : new JsonObject();
        fields["sessionId"] = sessionId;

        string? component = null;
        if (eventName == "ui.render")
        {
            component = fields["component"] is JsonValue value && value.TryGetValue<string>(out var given) ? given : null;
            if (component is null || !Components.Contains(component))
                return new BuiltEvent(default, null, $"A ui.render test needs a component: one of {string.Join(", ", Components)}.");

            if (fields["requestId"] is null)
                fields["requestId"] = $"test-{Interlocked.Increment(ref _testRequests)}";

            if (fields["props"] is not null and not JsonObject)
                return new BuiltEvent(default, component, "props must be an object.");
            var props = fields["props"] as JsonObject ?? (JsonObject)(fields["props"] = new JsonObject())!;

            switch (component)
            {
                case "ToolUse" or "ToolResult":
                    var tool = props["tool"] is JsonValue t && t.TryGetValue<string>(out var toolName) ? toolName : null;
                    Default(props, "rawTool", tool);
                    Default(props, "category", tool is "bash" or "shell" ? "shell" : "other");
                    Default(props, "status", "completed");
                    Default(props, "title", tool);
                    props["input"] ??= new JsonObject();
                    props["inputTruncated"] ??= false;
                    Default(props, "output", "");
                    props["outputTruncated"] ??= false;
                    break;
                case "ComposerBand":
                    props["isWorking"] ??= false;
                    break;
                case "Pane":
                    Default(props, "title", "");
                    break;
            }
        }

        using var document = JsonDocument.Parse(fields.ToJsonString());
        return new BuiltEvent(document.RootElement.Clone(), component, null);
    }

    private static void Default(JsonObject props, string key, string? value)
    {
        if (props[key] is null && value is not null)
            props[key] = value;
    }
}
