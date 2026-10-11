using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// The mod tools (<c>fleet_mod_write</c>, <c>_check</c>, <c>_reload</c>, <c>_keep</c>), for calls from a harness process.
/// The session is the one the call resolves to through <see cref="IHarnessCanvasCallerResolver"/>, as for the memory
/// tools; drafts are that session's, in its owner's folder. Every call checks the Mods switch and safe mode again: a
/// process started while Mods were on keeps its tools until it's recycled.
/// </summary>
public sealed class ModBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ModsFeature feature,
    ModsSafeMode safeMode,
    ModService mods,
    IModHost host)
{
    public const string TurnedOffMessage = "Mods are turned off in Fleet's Settings, so the mod tools can't run. Ask the user to turn on Mods.";
    public const string SafeModeRefusal = "Fleet was started without mods, so drafts don't load. The user can turn mods back on from the banner.";
    public const string SafeModeNote = "Fleet was started without mods, so the draft won't load until the user turns mods back on.";

    public Task<CanvasResult<CanvasToolOutput>> WriteAsync(
        string? bridgeToken, string? harnessSessionId, string? name, IReadOnlyList<ModFile>? files, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, needsDraft: false, loads: false, async gate =>
        {
            if (files is not { Count: > 0 } || files.Any(f => string.IsNullOrWhiteSpace(f.Path)))
                return Fail(CanvasErrorKind.Invalid, "Send the files as a list of {path, content}, at least one, each with a path.");

            var written = await mods.WriteDraftAsync(gate.SessionId, name!, files, ct).ConfigureAwait(false);
            if (written.IsFailure)
                return Fail(CanvasErrorKind.Invalid, written.Error.Description);

            var output = $"Wrote {string.Join(", ", files.Select(f => f.Path))} to the draft {name}.\n";
            if (gate.SafeMode)
                return Ok($"Wrote {name}", output + SafeModeNote);

            await ReloadDraftAsync(gate, name!, ct).ConfigureAwait(false);
            return Ok($"Wrote {name}", output + LoadResult(gate, written.Value));
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> CheckAsync(string? bridgeToken, string? harnessSessionId, string? name, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, needsDraft: true, loads: false, async gate =>
        {
            var check = await mods.CheckDraftAsync(gate.SessionId, name!, ct).ConfigureAwait(false);
            if (check.IsFailure)
                return Fail(CanvasErrorKind.Refused, check.Error.Description);

            return check.Value.Check is { } report
                ? Ok($"Checked {name}", Indented(report))
                : Fail(CanvasErrorKind.Refused, "No checker is available, so there is no check report.");
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> ReloadAsync(string? bridgeToken, string? harnessSessionId, string? name, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, needsDraft: true, loads: true, async gate =>
        {
            // Three strikes (or the user) turned it off: reloading after a fix is how the agent turns it back on.
            var draft = gate.Draft!;
            var wasOff = draft.Off is not null;
            if (wasOff)
            {
                var on = await mods.SetDraftOnAsync(gate.SessionId, name!, on: true, ct).ConfigureAwait(false);
                if (on.IsFailure)
                    return Fail(CanvasErrorKind.Invalid, on.Error.Description);
                draft = on.Value;
            }

            await ReloadDraftAsync(gate, name!, ct).ConfigureAwait(false);
            var output = LoadResult(gate, draft);
            return Ok($"Reloaded {name}", wasOff ? output + "\nIt was off; it's on again." : output);
        }, ct);

    public Task<CanvasResult<CanvasToolOutput>> KeepAsync(
        string? bridgeToken, string? harnessSessionId, string? name, string? note, CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, name, needsDraft: true, loads: true, async gate =>
        {
            var asked = await mods.RequestKeepAsync(gate.SessionId, name!, note, ct).ConfigureAwait(false);
            if (asked.IsFailure)
                return Fail(CanvasErrorKind.Invalid, asked.Error.Description);

            return Ok(
                $"Asked to keep {name}",
                $"Asked the user to keep {name}. The request is on the draft card in the conversation, where they'll see what it "
                + "touches and keep it or not. Don't ask again; the draft keeps running in this session meanwhile.");
        }, ct);

    // ── The checks every call makes ─────────────────────────────────────

    private sealed record Gate(string UserId, string SessionId, ModDraftView? Draft, bool SafeMode);

    /// <param name="loads">The tool needs the draft to run: refused in safe mode.</param>
    private async Task<CanvasResult<CanvasToolOutput>> RunAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? name,
        bool needsDraft,
        bool loads,
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
            if (inSafeMode && loads)
                return Fail(CanvasErrorKind.Refused, SafeModeRefusal);

            if (!ModNames.IsValid(name))
            {
                return Fail(
                    CanvasErrorKind.Invalid,
                    $"{name} isn't a mod name: use lowercase letters, digits and -, starting with a letter, 64 at most, not starting with fleet-.");
            }

            ModDraftView? draft = null;
            if (needsDraft)
            {
                var drafts = await mods.ListDraftsAsync(caller.FleetSessionId, ct).ConfigureAwait(false);
                draft = drafts.IsSuccess ? drafts.Value.FirstOrDefault(d => d.Name == name) : null;
                if (draft is null)
                    return Fail(CanvasErrorKind.NotFound, $"There's no draft {name} in this session. Write it with fleet_mod_write first.");
            }

            return await run(new Gate(caller.UserId, caller.FleetSessionId, draft, inSafeMode)).ConfigureAwait(false);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Has the host reload the draft, then tells clients, whose draft card shows the load's problem.</summary>
    private async Task ReloadDraftAsync(Gate gate, string name, CancellationToken ct)
    {
        await host.ReloadDraftAsync(gate.UserId, gate.SessionId, name, ct).ConfigureAwait(false);
        await mods.DraftLoadedAsync(gate.SessionId, name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What the host made of the draft after a reload: loaded, refused with the check's errors, or not running. The host
    /// skips a draft that's off or has no whole <c>mod.json</c> without a problem, so those are said first, never "loaded".
    /// </summary>
    private string LoadResult(Gate gate, ModDraftView draft)
    {
        var name = draft.Name;
        if (draft.Off is { } off)
        {
            return off.By == ModOffBy.Strikes
                ? $"{name} isn't loaded: it's off in this session after three failures in a row{(off.Error is { } error ? $" ({error})" : "")}. "
                  + "Once it's fixed, fleet_mod_reload turns it back on."
                : $"{name} isn't loaded: the user turned it off in this session. Ask them before turning it back on with fleet_mod_reload.";
        }

        // The view's version comes from mod.json: none means the store couldn't read a whole manifest.
        if (draft.Version is null)
            return $"{name} isn't loaded: its mod.json is missing or incomplete. It needs name, version, description and hooks.";

        if (host.GetLoadProblem(gate.UserId, $"{name}@draft:{gate.SessionId}") is { } problem)
            return $"{name} didn't load: {problem.Message}{Errors(problem.Report)}";

        var status = host.GetStatus(gate.UserId);
        return status.State == ModHostStates.Running
            ? $"{name} loaded in this session."
            : $"{name} isn't loaded: the mod host is {status.State}{(status.Reason is { } reason ? $" ({reason})" : "")}.";
    }

    /// <summary>The report's errors, one a line with where they are, as the agent fixes them.</summary>
    private static string Errors(JsonElement? report)
    {
        if (report is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array)
            return "";

        var text = new StringBuilder();
        foreach (var error in errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var at = error.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number
                ? $"{line.GetRawText()}:{(error.TryGetProperty("column", out var column) && column.ValueKind == JsonValueKind.Number ? column.GetRawText() : "1")}"
                : "-";
            text.Append("\nerror ").Append(at).Append(' ').Append(Text(error, "code")).Append(": ").Append(Text(error, "message"));
        }

        return text.ToString();
    }

    private static string Indented(JsonElement report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            report.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static CanvasResult<CanvasToolOutput> Ok(string title, string output) => CanvasResult.Ok(new CanvasToolOutput(title, output));

    private static CanvasResult<CanvasToolOutput> Fail(CanvasErrorKind kind, string message) => CanvasResult.Fail<CanvasToolOutput>(kind, message);

    // ── Reading the arguments, the same way for the plugins' calls and MCP ──

    /// <summary>
    /// The <c>files</c> argument as <c>{path, content}</c> entries, from a list or JSON text (some models send it so); null
    /// when it's missing or isn't a list. An entry without both is passed with an empty path, which the tool refuses.
    /// </summary>
    public static List<ModFile>? ReadFiles(JsonElement value)
    {
        var list = value;
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(value.GetString()!);
                list = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (list.ValueKind != JsonValueKind.Array)
            return null;

        return
        [
            .. list.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                && entry.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    ? new ModFile(path.GetString()!, content.GetString()!)
                    : new ModFile("", "")),
        ];
    }
}
