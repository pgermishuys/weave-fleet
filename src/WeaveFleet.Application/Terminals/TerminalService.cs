using System.Text.RegularExpressions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Terminals;

/// <summary>
/// Terminals for the current user's sessions. Checks the session belongs to the caller (the session
/// repository is scoped to the user) and that terminals are on, then hands over to <see cref="TerminalManager"/>.
/// Anything the caller can't see is <see cref="TerminalErrorKind.NotFound"/>.
/// <para>
/// Also the setup terminal: one shell per user in their home folder, where Fleet types a harness's installer
/// for them to run. It isn't part of any session, and only exists when Fleet runs on the user's own computer.
/// </para>
/// </summary>
public sealed partial class TerminalService(
    ISessionRepository sessions,
    TerminalManager manager,
    FleetOptions options,
    IUserContext user)
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

    /// <summary>Starts a new setup terminal in the user's home folder, ending the one they had.</summary>
    public async Task<TerminalResult<TerminalInfo>> CreateSetupAsync(int cols, int rows, CancellationToken ct = default)
    {
        var context = SetupContext();
        if (!context.IsSuccess)
            return TerminalResult.Fail<TerminalInfo>(context.Error);

        // One at a time: a setup terminal left from before (a closed wizard, a restart) would count against the limit.
        foreach (var previous in await manager.ListAsync(context.Value.SessionId, ct).ConfigureAwait(false))
            await manager.CloseAsync(context.Value, previous.Id, ct).ConfigureAwait(false);

        return await manager.CreateAsync(context.Value, cols, rows, ct).ConfigureAwait(false);
    }

    public async Task<TerminalResult<TerminalAttachment>> AttachSetupAsync(string terminalId, int cols, int rows, CancellationToken ct = default)
    {
        var context = SetupContext();
        return context.IsSuccess
            ? await manager.AttachAsync(context.Value, terminalId, cols, rows, ct).ConfigureAwait(false)
            : TerminalResult.Fail<TerminalAttachment>(context.Error);
    }

    /// <summary>Ends the setup terminal. Returns null when it's done, or why it couldn't be.</summary>
    public async Task<TerminalError?> CloseSetupAsync(string terminalId, CancellationToken ct = default)
    {
        var context = SetupContext();
        if (!context.IsSuccess)
            return context.Error;

        return await manager.CloseAsync(context.Value, terminalId, ct).ConfigureAwait(false)
            ? null
            : new TerminalError(TerminalErrorKind.NotFound, $"Terminal {terminalId} not found.");
    }

    private TerminalResult<TerminalContext> SetupContext()
    {
        if (!options.TerminalEnabled)
            return TerminalResult.Fail<TerminalContext>(TerminalErrorKind.NotFound, "Terminals are turned off.");

        // In cloud mode a shell would run on the server, not on the user's computer.
        if (options.Cloud.Enabled)
            return TerminalResult.Fail<TerminalContext>(TerminalErrorKind.NotFound, "Harnesses can only be set up from Fleet when it runs on your own computer.");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return TerminalResult.Ok(new TerminalContext(SetupSessionId(user.UserId), user.UserId, home));
    }

    /// <summary>The key a user's setup terminal is kept under, in place of a session id; safe as a folder name.</summary>
    internal static string SetupSessionId(string userId) => "setup-" + UnsafeIdCharacter().Replace(userId, "_");

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex UnsafeIdCharacter();

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
