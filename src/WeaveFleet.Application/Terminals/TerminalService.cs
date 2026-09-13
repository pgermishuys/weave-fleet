using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Terminals;

/// <summary>
/// Terminals for the current user's sessions. Checks the session belongs to the caller (the session
/// repository is scoped to the user) and that terminals are on, then hands over to <see cref="TerminalManager"/>.
/// Anything the caller can't see is <see cref="TerminalErrorKind.NotFound"/>.
/// </summary>
public sealed class TerminalService(ISessionRepository sessions, TerminalManager manager, FleetOptions options)
{
    public async Task<TerminalResult<IReadOnlyList<TerminalInfo>>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var context = await ContextAsync(sessionId).ConfigureAwait(false);
        if (!context.IsSuccess)
            return TerminalResult.Fail<IReadOnlyList<TerminalInfo>>(context.Error);

        return TerminalResult.Ok(await manager.ListAsync(sessionId, ct).ConfigureAwait(false));
    }

    public async Task<TerminalResult<TerminalInfo>> CreateAsync(string sessionId, int cols, int rows, CancellationToken ct = default)
    {
        var context = await ContextAsync(sessionId, forNewShell: true).ConfigureAwait(false);
        return context.IsSuccess
            ? await manager.CreateAsync(context.Value, cols, rows, ct).ConfigureAwait(false)
            : TerminalResult.Fail<TerminalInfo>(context.Error);
    }

    public async Task<TerminalResult<TerminalAttachment>> AttachAsync(string sessionId, string terminalId, int cols, int rows, CancellationToken ct = default)
    {
        var context = await ContextAsync(sessionId, forNewShell: true).ConfigureAwait(false);
        return context.IsSuccess
            ? await manager.AttachAsync(context.Value, terminalId, cols, rows, ct).ConfigureAwait(false)
            : TerminalResult.Fail<TerminalAttachment>(context.Error);
    }

    /// <summary>Ends the terminal and deletes its scrollback. Returns null when it's done, or why it couldn't be.</summary>
    public async Task<TerminalError?> CloseAsync(string sessionId, string terminalId, CancellationToken ct = default)
    {
        var context = await ContextAsync(sessionId).ConfigureAwait(false);
        if (!context.IsSuccess)
            return context.Error;

        return await manager.CloseAsync(context.Value, terminalId, ct).ConfigureAwait(false)
            ? null
            : new TerminalError(TerminalErrorKind.NotFound, $"Terminal {terminalId} not found.");
    }

    private async Task<TerminalResult<TerminalContext>> ContextAsync(string sessionId, bool forNewShell = false)
    {
        if (!options.TerminalEnabled)
            return TerminalResult.Fail<TerminalContext>(TerminalErrorKind.NotFound, "Terminals are turned off.");

        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null)
            return TerminalResult.Fail<TerminalContext>(TerminalErrorKind.NotFound, $"Session {sessionId} not found.");

        if (forNewShell && string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return TerminalResult.Fail<TerminalContext>(TerminalErrorKind.Unavailable, "This session is archived, so it can't open terminals.");

        return TerminalResult.Ok(new TerminalContext(session.Id, session.UserId, session.Directory));
    }
}
