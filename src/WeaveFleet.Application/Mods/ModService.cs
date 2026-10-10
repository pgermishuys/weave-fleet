using System.Text.Json;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Mods;

/// <summary>Every kept mod of the user, and whether "Start without mods" is set.</summary>
public sealed record ModsView(bool SafeMode, IReadOnlyList<ModView> Mods);

/// <summary>A kept mod as the API shows it. Description and version come from the active version's <c>mod.json</c>.</summary>
public sealed record ModView(
    string Name,
    string? Description,
    int? Active,
    string? ActiveVersion,
    ModOff? Off,
    IReadOnlyList<ModVersion> Versions);

/// <summary>A mod the agent is writing in a session.</summary>
/// <param name="Kept">The active version of the kept mod of the same name; null when none is kept.</param>
/// <param name="KeepRequest">The agent's ask to keep the draft, while the user hasn't answered; null when there is none.</param>
/// <param name="Problem">Why the draft last failed to load, from the mod host; null when it loaded or never tried.</param>
public sealed record ModDraftView(
    string SessionId,
    string Name,
    string? Description,
    string? Version,
    ModOff? Off,
    int? Kept,
    ModKeepRequestView? KeepRequest = null,
    ModDraftProblemView? Problem = null);

/// <summary>The first error of a draft's failed load: its code, message and line when the check gave one.</summary>
public sealed record ModDraftProblemView(string Code, string Message, int? Line);

/// <summary>A version's or a draft's files, for Show code.</summary>
public sealed record ModFilesView(IReadOnlyList<ModFile> Files);

/// <summary>The static check of a draft; null when no checker is available.</summary>
public sealed record ModCheckView(JsonElement? Check);

/// <summary>
/// The user's mods: what they've kept, which version is active, whether it's on, and the drafts the agent is writing.
/// Every change raises <c>mods.changed</c> once, after the store has written it, so clients refetch.
/// </summary>
public sealed class ModService(
    IModVersionStore store,
    IModChecker checker,
    IEventBroadcaster events,
    IUserContext user,
    ModsSafeMode safeMode,
    ISessionRepository sessions,
    TimeProvider clock,
    IModDraftRunner runner,
    ModKeepRequests keepRequests)
{
    public const int MaxNoteLength = 2000;

    private const string Mod = "Mod";
    private const string Draft = "ModDraft";

    private string UserId => user.UserId;

    // ── Kept mods ───────────────────────────────────────────────────────

    public async Task<ModsView> ListAsync(CancellationToken ct = default)
    {
        var histories = await store.ListAsync(UserId, ct).ConfigureAwait(false);
        var mods = new List<ModView>();
        foreach (var history in histories.Where(h => h.Versions.Count > 0))
            mods.Add(await ToViewAsync(history, ct).ConfigureAwait(false));
        return new ModsView(safeMode.IsOn(UserId), mods);
    }

    public async Task<Result<ModView>> GetAsync(string name, CancellationToken ct = default)
    {
        var found = await FindAsync(name, ct).ConfigureAwait(false);
        return found.IsFailure ? found.Error : await ToViewAsync(found.Value, ct).ConfigureAwait(false);
    }

    public async Task<Result<ModFilesView>> ReadVersionFilesAsync(string name, int number, CancellationToken ct = default)
    {
        if (!ModNames.IsValid(name))
            return FleetError.NotFoundFor(Mod, name);

        var files = await store.ReadVersionFilesAsync(UserId, name, number, ct).ConfigureAwait(false);
        return files is null ? FleetError.NotFoundFor("ModVersion", $"{name} v{number}") : new ModFilesView(files);
    }

    /// <summary>Makes <paramref name="number"/> the version sessions load, and turns the mod on.</summary>
    public async Task<Result<ModView>> UseVersionAsync(string name, int number, CancellationToken ct = default)
    {
        var found = await FindAsync(name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;
        if (found.Value.Versions.All(v => v.Number != number))
            return FleetError.NotFoundFor("ModVersion", $"{name} v{number}");

        return await ChangeAsync(name, "version", sessionId: null, () => store.UseVersionAsync(UserId, name, number, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// On a version after the first: makes the previous version active and turns the mod on. On the first: turns the mod
    /// off, leaving it active. Nothing is deleted.
    /// </summary>
    public async Task<Result<ModView>> UndoAsync(string name, CancellationToken ct = default)
    {
        // The read is for the NotFound answer only; the store decides and writes in one step.
        var found = await FindAsync(name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;

        return await ChangeAsync(name, "undone", sessionId: null, () => store.UndoAsync(UserId, name, clock.GetUtcNow(), ct), ct).ConfigureAwait(false);
    }

    public async Task<Result<ModView>> SetOnAsync(string name, bool on, CancellationToken ct = default)
    {
        if ((await FindAsync(name, ct).ConfigureAwait(false)) is { IsFailure: true } missing)
            return missing.Error;

        return await ChangeAsync(name, on ? "on" : "off", sessionId: null,
            () => store.SetOffAsync(UserId, name, on ? null : new ModOff(ModOffBy.User, clock.GetUtcNow()), ct), ct).ConfigureAwait(false);
    }

    /// <summary>Three failures in a row: the mod host turns the mod off and keeps the last error to show.</summary>
    public async Task<Result<ModView>> RecordStrikesAsync(string name, string error, CancellationToken ct = default)
    {
        if ((await FindAsync(name, ct).ConfigureAwait(false)) is { IsFailure: true } missing)
            return missing.Error;

        return await ChangeAsync(name, "strikes", sessionId: null,
            () => store.SetOffAsync(UserId, name, new ModOff(ModOffBy.Strikes, clock.GetUtcNow(), error), ct), ct).ConfigureAwait(false);
    }

    // ── Drafts ──────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ModDraftView>>> ListDraftsAsync(string sessionId, CancellationToken ct = default)
    {
        if (!ModNames.IsValidSessionId(sessionId))
            return FleetError.NotFoundFor("Session", sessionId);

        var views = new List<ModDraftView>();
        foreach (var draft in await store.ListDraftsAsync(UserId, sessionId, ct).ConfigureAwait(false))
            views.Add(await ToViewAsync(draft, ct).ConfigureAwait(false));
        return views;
    }

    public async Task<Result<ModFilesView>> ReadDraftFilesAsync(string sessionId, string name, CancellationToken ct = default)
    {
        if (!ModNames.IsValidSessionId(sessionId) || !ModNames.IsValid(name))
            return FleetError.NotFoundFor(Draft, name);

        var files = await store.ReadDraftFilesAsync(UserId, sessionId, name, ct).ConfigureAwait(false);
        return files is null ? FleetError.NotFoundFor(Draft, name) : new ModFilesView(files);
    }

    /// <summary>Runs the static check on the draft's folder.</summary>
    public async Task<Result<ModCheckView>> CheckDraftAsync(string sessionId, string name, CancellationToken ct = default)
    {
        var found = await FindDraftAsync(sessionId, name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;

        try
        {
            // On a staged copy, as Keep does: the checker never reads the live draft, which the agent may be writing.
            return new ModCheckView(await store.CheckDraftAsync(UserId, sessionId, name, checker.CheckAsync, ct).ConfigureAwait(false));
        }
        catch (ModStoreException e)
        {
            return FleetError.ValidationError("Check", e.Message);
        }
    }

    /// <summary>Writes the files into the session's draft, creating the draft when it's new. A file sent replaces the one there.</summary>
    public async Task<Result<ModDraftView>> WriteDraftAsync(string sessionId, string name, IReadOnlyList<ModFile> files, CancellationToken ct = default)
    {
        if (!ModNames.IsValidSessionId(sessionId))
            return FleetError.NotFoundFor("Session", sessionId);
        if (!ModNames.IsValid(name))
            return FleetError.ValidationError("Name", $"{name} isn't a mod name.");
        if (files.Count == 0)
            return FleetError.ValidationError("Files", "Send at least one file.");

        ModDraft draft;
        try
        {
            draft = await store.WriteDraftFilesAsync(UserId, sessionId, name, files, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ModStoreException or ModStoreFullException)
        {
            return FleetError.ValidationError("ModDraft", e.Message);
        }

        await RaiseAsync("draft-written", name, sessionId, ct).ConfigureAwait(false);
        return await ToViewAsync(draft, ct).ConfigureAwait(false);
    }

    /// <summary>Turning a draft off ends the agent's ask to keep it, as a decline.</summary>
    public async Task<Result<ModDraftView>> SetDraftOnAsync(string sessionId, string name, bool on, CancellationToken ct = default)
    {
        var result = await ChangeDraftAsync(sessionId, name, on ? "draft-on" : "draft-off",
            () => store.SetDraftOffAsync(UserId, sessionId, name, on ? null : new ModOff(ModOffBy.User, clock.GetUtcNow()), ct), ct).ConfigureAwait(false);
        if (result.IsSuccess && !on)
            keepRequests.Resolve(UserId, sessionId, name, new ModKeepDecision(ModKeepOutcome.Declined));
        return result;
    }

    /// <summary>
    /// The agent asks the user to keep the draft; the card shows it until they answer. The caller waits on the returned
    /// request. A new request replaces the one before it.
    /// </summary>
    public async Task<Result<ModKeepRequest>> RequestKeepAsync(string sessionId, string name, string? note, CancellationToken ct = default)
    {
        if (note is { Length: > MaxNoteLength })
            return FleetError.ValidationError("Note", $"The note is at most {MaxNoteLength} characters.");
        var found = await FindDraftAsync(sessionId, name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;

        var request = keepRequests.Request(UserId, sessionId, name, string.IsNullOrWhiteSpace(note) ? null : note);
        await RaiseAsync("keep-requested", name, sessionId, ct).ConfigureAwait(false);
        return request;
    }

    /// <summary>The user declines the agent's ask to keep the draft. The draft stays in its session, on.</summary>
    public async Task<Result<ModDraftView>> DeclineKeepAsync(string sessionId, string name, CancellationToken ct = default)
    {
        var found = await FindDraftAsync(sessionId, name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;

        keepRequests.Resolve(UserId, sessionId, name, new ModKeepDecision(ModKeepOutcome.Declined));
        await RaiseAsync("keep-declined", name, sessionId, ct).ConfigureAwait(false);
        return await ToViewAsync(found.Value, ct).ConfigureAwait(false);
    }

    /// <summary>The mod's log from the mod host: the session's draft when <paramref name="sessionId"/> is given, the kept mod otherwise.</summary>
    public Result<IReadOnlyList<ModDraftLogLine>> ReadLog(string name, string? sessionId)
    {
        if (!ModNames.IsValid(name) || (sessionId is not null && !ModNames.IsValidSessionId(sessionId)))
            return FleetError.NotFoundFor(Mod, name);
        return runner.Log(UserId, name, sessionId).ToList();
    }

    /// <summary>Three failures in a row in a draft: the draft is turned off in its session.</summary>
    public async Task<Result<ModDraftView>> RecordDraftStrikesAsync(string sessionId, string name, string error, CancellationToken ct = default)
        => await ChangeDraftAsync(sessionId, name, "strikes",
            () => store.SetDraftOffAsync(UserId, sessionId, name, new ModOff(ModOffBy.Strikes, clock.GetUtcNow(), error), ct), ct).ConfigureAwait(false);

    /// <summary>
    /// Keeps the session's draft as the mod's next version and makes it active. <paramref name="check"/> is the report
    /// the user saw; without one, Fleet checks the draft now. A report that isn't ok stops the Keep.
    /// </summary>
    public async Task<Result<ModView>> KeepAsync(string sessionId, string name, string? note, JsonElement? check = null, CancellationToken ct = default)
    {
        if (note is { Length: > MaxNoteLength })
            return FleetError.ValidationError("Note", $"The note is at most {MaxNoteLength} characters.");

        var found = await FindDraftAsync(sessionId, name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;

        var title = (await sessions.GetByIdAsync(sessionId).ConfigureAwait(false))?.Title;
        ModVersion version;
        // The store checks its own staged copy, so a draft that changes meanwhile isn't what gets checked.
        ModKeepCheck keepCheck = check is { } given
            ? (_, _) => Task.FromResult<JsonElement?>(given)
            : (staged, token) => checker.CheckAsync(staged, token);
        try
        {
            version = await store.KeepAsync(UserId, sessionId, name, new ModKeepSource(title, note), keepCheck, ct).ConfigureAwait(false);
        }
        catch (ModStoreException e)
        {
            return FleetError.ValidationError("Keep", e.Message);
        }

        keepRequests.Resolve(UserId, sessionId, name, new ModKeepDecision(ModKeepOutcome.Kept, version.Number));
        await RaiseAsync("kept", name, sessionId, ct).ConfigureAwait(false);
        return await ToViewAsync(await store.GetAsync(UserId, name, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    // ── Safe mode ───────────────────────────────────────────────────────

    /// <summary>"Start without mods" for the current user: none of their mods run until Fleet restarts or this is turned off.</summary>
    public async Task<ModsView> SetSafeModeAsync(bool on, CancellationToken ct = default)
    {
        safeMode.Set(UserId, on);
        await RaiseAsync("safe-mode", name: null, sessionId: null, ct).ConfigureAwait(false);
        return await ListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The user turned the Mods switch from off to on: that turns mods back on, so it ends safe mode.</summary>
    public async Task SwitchedOnAsync(CancellationToken ct = default)
    {
        if (!safeMode.IsOn(UserId))
            return;
        safeMode.Set(UserId, false);
        await RaiseAsync("safe-mode", name: null, sessionId: null, ct).ConfigureAwait(false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<Result<ModHistory>> FindAsync(string name, CancellationToken ct)
    {
        if (!ModNames.IsValid(name))
            return FleetError.NotFoundFor(Mod, name);

        var history = await store.GetAsync(UserId, name, ct).ConfigureAwait(false);
        return history.Versions.Count == 0 ? FleetError.NotFoundFor(Mod, name) : history;
    }

    private async Task<Result<ModDraft>> FindDraftAsync(string sessionId, string name, CancellationToken ct)
    {
        if (!ModNames.IsValidSessionId(sessionId) || !ModNames.IsValid(name))
            return FleetError.NotFoundFor(Draft, name);

        return await store.GetDraftAsync(UserId, sessionId, name, ct).ConfigureAwait(false) is { } draft
            ? draft
            : FleetError.NotFoundFor(Draft, name);
    }

    /// <summary>Runs a write on a kept mod; when the store took it, raises the event and answers with the mod as it is now.</summary>
    private async Task<Result<ModView>> ChangeAsync(string name, string reason, string? sessionId, Func<Task<ModHistory>> write, CancellationToken ct)
    {
        ModHistory history;
        try
        {
            history = await write().ConfigureAwait(false);
        }
        catch (ModStoreException e)
        {
            return FleetError.ValidationError("Mod", e.Message);
        }

        await RaiseAsync(reason, name, sessionId, ct).ConfigureAwait(false);
        return await ToViewAsync(history, ct).ConfigureAwait(false);
    }

    private async Task<Result<ModDraftView>> ChangeDraftAsync(string sessionId, string name, string reason, Func<Task> write, CancellationToken ct)
    {
        var found = await FindDraftAsync(sessionId, name, ct).ConfigureAwait(false);
        if (found.IsFailure)
            return found.Error;

        try
        {
            await write().ConfigureAwait(false);
        }
        catch (ModStoreException e)
        {
            return FleetError.ValidationError("ModDraft", e.Message);
        }

        await RaiseAsync(reason, name, sessionId, ct).ConfigureAwait(false);
        var now = await store.GetDraftAsync(UserId, sessionId, name, ct).ConfigureAwait(false) ?? found.Value;
        return await ToViewAsync(now, ct).ConfigureAwait(false);
    }

    private async Task<ModView> ToViewAsync(ModHistory history, CancellationToken ct)
    {
        ModManifest? manifest = null;
        if (history.Active is { } active)
            manifest = await store.ReadVersionManifestAsync(UserId, history.Name, active, ct).ConfigureAwait(false);

        return new ModView(
            history.Name,
            manifest?.Description,
            history.Active,
            manifest?.Version ?? history.ActiveVersion?.Version,
            history.Off,
            history.Versions.OrderBy(v => v.Number).ToList());
    }

    private async Task<ModDraftView> ToViewAsync(ModDraft draft, CancellationToken ct)
    {
        var manifest = draft.Manifest;
        var kept = await store.GetAsync(UserId, draft.Name, ct).ConfigureAwait(false);
        return new ModDraftView(
            draft.SessionId,
            draft.Name,
            manifest?.Description,
            manifest?.Version,
            draft.Off,
            kept.Active,
            keepRequests.Get(UserId, draft.SessionId, draft.Name),
            ProblemOf(runner.LoadProblem(UserId, draft.SessionId, draft.Name)));
    }

    /// <summary>The first error of the failed load's report, or a plain <c>load</c> problem with the host's message.</summary>
    private static ModDraftProblemView? ProblemOf(ModDraftProblem? problem)
    {
        if (problem is null)
            return null;

        if (problem.Report is { ValueKind: JsonValueKind.Object } report
            && report.TryGetProperty("errors", out var errors)
            && errors is { ValueKind: JsonValueKind.Array }
            && errors.GetArrayLength() > 0
            && errors[0] is { ValueKind: JsonValueKind.Object } first
            && first.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            var code = first.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "load";
            int? line = first.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) ? n : null;
            return new ModDraftProblemView(code, message.GetString()!, line);
        }

        return new ModDraftProblemView("load", problem.Message, null);
    }

    private async Task RaiseAsync(string reason, string? name, string? sessionId, CancellationToken ct)
    {
        var payload = new ModsChangedPayload { Name = name, Reason = reason, SessionId = sessionId };
        await events.BroadcastAsync(
            "sessions",
            EventTypes.ModsChanged,
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.ModsChangedPayload),
            new ModsChanged { Payload = payload },
            UserId,
            ct).ConfigureAwait(false);
    }
}
