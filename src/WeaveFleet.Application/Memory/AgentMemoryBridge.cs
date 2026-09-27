using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

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

            var saved = await memory.SaveFromAgentAsync(session.Value, list, text, kind, replaces, ct).ConfigureAwait(false);
            if (saved.IsFailure)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, saved.Error.Description);

            var note = saved.Value.Note;
            var where = note.List == MemoryList.Machine
                ? "this machine"
                : AgentMemory.RepositoryName(note.Repository ?? session.Value.Directory);
            if (saved.Value.AlreadyKnown)
                return CanvasResult.Ok(new CanvasToolOutput($"Already remembered for {where}", $"Note {note.Id} already says this; it's now dated today. Nothing new was saved."));

            // The first line is what the conversation shows under the call: the note itself.
            var output = saved.Value.Replaced is { } replaced && replaced != note.Id
                ? $"{note.Text}\nSaved as note {note.Id} for {where}, in place of {replaced}. Sessions read it from their next request."
                : $"{note.Text}\nSaved as note {note.Id} for {where}. Sessions read it from their next request.";
            return CanvasResult.Ok(new CanvasToolOutput($"Remembered for {where}", output));
        }
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
