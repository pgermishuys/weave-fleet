using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Application.Memory;

/// <summary>
/// The memory tools (<c>fleet_memory_save</c>, <c>fleet_memory_forget</c>), for calls from a harness process. The
/// session is the one the call resolves to through <see cref="IHarnessCanvasCallerResolver"/>, as for the canvas tools,
/// and the note is saved for its owner and its repository.
/// </summary>
public sealed class AgentMemoryBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    AgentMemoryService memory,
    SessionService sessions)
{
    public const string TurnedOffMessage = "Memory is turned off in Fleet's Settings, so nothing was saved.";

    public async Task<CanvasResult<CanvasToolOutput>> SaveAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? list,
        string? text,
        string? kind,
        string? replaces,
        string? how = null,
        CancellationToken ct = default)
    {
        if (await ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false) is not { } caller)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
        {
            // Checked on every call: a process started while it was on keeps the tool until it's recycled.
            if (!await memory.IsEnabledAsync().ConfigureAwait(false))
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, TurnedOffMessage);

            var session = await sessions.GetSessionAsync(caller.FleetSessionId).ConfigureAwait(false);
            if (session.IsFailure)
                return UnknownCaller();

            var saved = await memory.SaveFromAgentAsync(session.Value, list, text, kind, replaces, how, ct).ConfigureAwait(false);
            if (saved.IsFailure)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, saved.Error.Description);

            var note = saved.Value.Note;
            var where = note.List == MemoryList.Machine
                ? "this machine"
                : AgentMemory.RepositoryName(note.Repository ?? session.Value.Directory);
            if (saved.Value.AlreadyKnown)
                return CanvasResult.Ok(new CanvasToolOutput($"Already remembered for {where}", $"Note {note.Id} already says this; it's now dated today. Nothing new was saved."));

            // The first line is what the conversation shows under the call: the note itself.
            if (saved.Value.Relearned)
            {
                return CanvasResult.Ok(new CanvasToolOutput(
                    $"Remembered for {where}",
                    $"{note.Text}\nThis lesson was learned before and its note had expired, so note {note.Id} is back for {where}, "
                    + $"now for {note.Lifetime} days of use. Other sessions get it with their next prompt."));
            }

            var output = saved.Value.Replaced is { } replaced && replaced != note.Id
                ? $"{note.Text}\nSaved as note {note.Id} for {where}, in place of {replaced}. Other sessions get it with their next prompt."
                : $"{note.Text}\nSaved as note {note.Id} for {where}. Other sessions get it with their next prompt.";
            // The group every session reads was full: say which notes now wait in search instead.
            if (saved.Value.MovedDown is { Count: > 0 } moved)
            {
                output += "\nThe notes every session reads were full, so " + string.Join("; ", moved.Select(other => $"note {other.Id} (\"{Shorten(other.Text)}\")"))
                          + " moved to the notes found by search. They are still kept: `fleet_memory_search` finds them.";
            }
            return CanvasResult.Ok(new CanvasToolOutput($"Remembered for {where}", output));
        }
    }

    /// <summary><c>fleet_memory_search</c>: notes of this repository and/or machine that match, from both groups.</summary>
    public async Task<CanvasResult<CanvasToolOutput>> SearchAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? query,
        string? list,
        CancellationToken ct = default)
    {
        if (await ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false) is not { } caller)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
        {
            if (!await memory.IsSearchOnAsync().ConfigureAwait(false))
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, SearchTurnedOffMessage);

            var session = await sessions.GetSessionAsync(caller.FleetSessionId).ConfigureAwait(false);
            if (session.IsFailure)
                return UnknownCaller();

            var found = await memory.SearchFromAgentAsync(session.Value, query, list, ct).ConfigureAwait(false);
            if (found.IsFailure)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, found.Error.Description);

            var notes = found.Value.Notes;
            if (notes.Count == 0)
            {
                return CanvasResult.Ok(new CanvasToolOutput(
                    "No notes found",
                    $"No note matches \"{query!.Trim()}\" ({found.Value.Searched} searched). Try other words, or the other list."));
            }

            var lines = notes.Select(note =>
            {
                var status = note.Kind == MemoryKinds.Learned
                    ? (AgentMemory.CleanHow(note.How) is { } how ? "tested: " + how + "; " : "untested; ")
                    : "";
                var group = MemoryTiers.IsAlways(note) ? "read by every session" : "found by search";
                var where = note.List == MemoryList.Machine ? "machine" : "repository";
                return $"- [{note.Id}] {Shorten(note.Text, int.MaxValue)} ({status}{where}, {group}, {note.Updated.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)})";
            });
            return CanvasResult.Ok(new CanvasToolOutput(
                notes.Count == 1 ? "Found 1 note" : $"Found {notes.Count} notes",
                string.Join('\n', lines)));
        }
    }

    public const string SearchTurnedOffMessage = "Searching older notes is turned off in Fleet's Settings → Labs, so there is nothing to search.";

    private static string Shorten(string text, int max = 60)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    public async Task<CanvasResult<CanvasToolOutput>> ForgetAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? id,
        CancellationToken ct = default)
    {
        if (await ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false) is not { } caller)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
        {
            if (!await memory.IsEnabledAsync().ConfigureAwait(false))
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, TurnedOffMessage);

            var session = await sessions.GetSessionAsync(caller.FleetSessionId).ConfigureAwait(false);
            if (session.IsFailure)
                return UnknownCaller();

            var forgotten = await memory.ForgetFromAgentAsync(session.Value, id, ct).ConfigureAwait(false);
            if (forgotten.IsFailure)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, $"There's no note {id} for this repository or this machine. Use an id from the list in your instructions.");

            return CanvasResult.Ok(new CanvasToolOutput("Forgot a note", $"Forgot note {forgotten.Value.Id}: {forgotten.Value.Text}"));
        }
    }

    private async Task<HarnessCanvasCaller?> ResolveAsync(string? bridgeToken, string? harnessSessionId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId)
            ? null
            : await callers.ResolveAsync(bridgeToken, harnessSessionId, ct).ConfigureAwait(false);

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}
