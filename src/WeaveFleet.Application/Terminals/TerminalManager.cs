using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Terminals;

/// <summary>Ends a session's terminals when the session is archived or deleted.</summary>
public interface ISessionTerminalCleanup
{
    /// <summary>Ends every shell in the session and deletes its saved terminals and scrollback.</summary>
    Task EndSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Every running shell in Fleet, keyed by session and terminal. Callers check that the session belongs to the
/// current user first (<see cref="TerminalService"/> does); this class trusts the <see cref="TerminalContext"/>.
/// </summary>
public sealed partial class TerminalManager(
    IPtyFactory ptys,
    ITerminalHistoryStore store,
    IEventBroadcaster events,
    FleetOptions options,
    ILogger<TerminalManager> logger,
    TimeProvider? timeProvider = null) : ISessionTerminalCleanup, IAsyncDisposable
{
    /// <summary>Written between the old scrollback and a new shell when a saved terminal is reopened.</summary>
    internal static readonly byte[] RestartDivider = Encoding.UTF8.GetBytes("\r\n\u001b[2m— Fleet restarted —\u001b[0m\r\n");

    private readonly ConcurrentDictionary<(string SessionId, string TerminalId), LiveTerminal> _live = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Lets tests stand in for the file system and the process environment.</summary>
    internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
    internal Func<string, bool> FileExists { get; init; } = File.Exists;
    internal Func<System.Collections.IDictionary> ProcessEnvironment { get; init; } = Environment.GetEnvironmentVariables;

    public int LiveCount => _live.Count;

    public async Task<IReadOnlyList<TerminalInfo>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var saved = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
        return [.. saved.Select(s => _live.TryGetValue((sessionId, s.Id), out var live)
            ? live.Info
            : new TerminalInfo(s.Id, s.Title, TerminalStatus.Stopped, s.CreatedAt))];
    }

    public async Task<TerminalResult<TerminalInfo>> CreateAsync(TerminalContext context, int cols, int rows, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = await store.ListAsync(context.SessionId, ct).ConfigureAwait(false);
            if (saved.Count >= options.Terminal.MaxTerminalsPerSession)
            {
                return TerminalResult.Fail<TerminalInfo>(TerminalErrorKind.LimitReached,
                    $"This session already has {saved.Count} terminals. Close one to open another.");
            }

            var ready = CheckCanStart(context);
            if (ready is not null)
                return TerminalResult.Fail<TerminalInfo>(ready);

            var id = "t_" + Guid.CreateVersion7().ToString("N");
            var started = await StartAsync(context, id, title: null, _time.GetUtcNow(), cols, rows, history: null, saved, ct).ConfigureAwait(false);
            if (!started.IsSuccess)
                return TerminalResult.Fail<TerminalInfo>(started.Error);

            var terminal = started.Value;
            await store.SaveTerminalAsync(context.SessionId, new SavedTerminal(terminal.Id, terminal.Title, terminal.CreatedAt), ct).ConfigureAwait(false);
            await BroadcastAsync(context, "terminal.opened", terminal.Id, terminal.Title, exitCode: null, ct).ConfigureAwait(false);
            return TerminalResult.Ok(terminal.Info);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attaches a client to a terminal, first starting a new shell under the saved scrollback when the terminal
    /// was saved before Fleet restarted. The terminal takes the client's size.
    /// </summary>
    public async Task<TerminalResult<TerminalAttachment>> AttachAsync(
        TerminalContext context,
        string terminalId,
        int cols,
        int rows,
        CancellationToken ct = default)
    {
        if (_live.TryGetValue((context.SessionId, terminalId), out var running))
        {
            running.Resize(cols, rows);
            return TerminalResult.Ok(running.Attach());
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_live.TryGetValue((context.SessionId, terminalId), out running))
            {
                running.Resize(cols, rows);
                return TerminalResult.Ok(running.Attach());
            }

            var all = await store.ListAsync(context.SessionId, ct).ConfigureAwait(false);
            var saved = all.FirstOrDefault(t => t.Id == terminalId);
            if (saved is null)
                return TerminalResult.Fail<TerminalAttachment>(TerminalErrorKind.NotFound, $"Terminal {terminalId} not found.");

            var ready = CheckCanStart(context);
            if (ready is not null)
                return TerminalResult.Fail<TerminalAttachment>(ready);

            var history = await store.ReadHistoryAsync(context.SessionId, terminalId, ct).ConfigureAwait(false);
            var replay = history is { Length: > 0 } ? [.. history, .. RestartDivider] : Array.Empty<byte>();
            var started = await StartAsync(context, saved.Id, saved.Title, saved.CreatedAt, cols, rows, replay, all, ct).ConfigureAwait(false);
            return started.IsSuccess
                ? TerminalResult.Ok(started.Value.Attach())
                : TerminalResult.Fail<TerminalAttachment>(started.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the terminal's shell and forgets it. Returns false when there was no such terminal.</summary>
    public async Task<bool> CloseAsync(TerminalContext context, string terminalId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = (await store.ListAsync(context.SessionId, ct).ConfigureAwait(false)).FirstOrDefault(t => t.Id == terminalId);
            _live.TryRemove((context.SessionId, terminalId), out var live);
            if (saved is null && live is null)
                return false;

            if (live is not null)
                await live.KillAsync().ConfigureAwait(false);
            await store.RemoveTerminalAsync(context.SessionId, terminalId, ct).ConfigureAwait(false);
            await BroadcastAsync(context, "terminal.closed", terminalId, saved?.Title ?? live!.Title, exitCode: null, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var key in _live.Keys.Where(k => k.SessionId == sessionId).ToList())
            {
                if (_live.TryRemove(key, out var live))
                    await live.KillAsync().ConfigureAwait(false);
            }
            await store.DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves every terminal's scrollback and ends every shell. The terminals come back as stopped.</summary>
    public async Task ShutdownAsync()
    {
        var all = _live.Values.ToList();
        _live.Clear();
        await Task.WhenAll(all.Select(t => t.StopForShutdownAsync())).ConfigureAwait(false);
    }

    /// <summary>Ends anything still running; <see cref="ShutdownAsync"/> normally ran first and saved it.</summary>
    public async ValueTask DisposeAsync()
    {
        var all = _live.Values.ToList();
        _live.Clear();
        foreach (var terminal in all)
            await terminal.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private TerminalError? CheckCanStart(TerminalContext context)
    {
        if (_live.Count >= options.Terminal.MaxLiveTerminals)
        {
            return new TerminalError(TerminalErrorKind.LimitReached,
                $"Fleet is already running {_live.Count} terminals. Close some, in this session or others, to open another.");
        }
        if (string.IsNullOrWhiteSpace(context.Directory) || !DirectoryExists(context.Directory))
        {
            return new TerminalError(TerminalErrorKind.Unavailable,
                $"The session's folder {context.Directory} doesn't exist any more, so there's nowhere to start a shell.");
        }
        return null;
    }

    private async Task<TerminalResult<LiveTerminal>> StartAsync(
        TerminalContext context,
        string id,
        string? title,
        DateTimeOffset createdAt,
        int cols,
        int rows,
        byte[]? history,
        IReadOnlyList<SavedTerminal> existing,
        CancellationToken ct)
    {
        var windows = OperatingSystem.IsWindows();
        var env = TerminalEnvironment.Build(ProcessEnvironment(), windows);
        var candidates = TerminalShell.Candidates(env, windows, FileExists);

        foreach (var shell in candidates)
        {
            IPtyProcess process;
            try
            {
                process = await ptys.SpawnAsync(
                    new PtySpawnOptions(shell.Path, shell.Args, context.Directory, Math.Clamp(cols, 2, 1000), Math.Clamp(rows, 1, 500), env),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSpawnFailed(logger, ex, shell.Path);
                continue;
            }

            var scrollback = new TerminalHistory(options.Terminal.MaxHistoryLines, options.Terminal.MaxHistoryBytes);
            if (history is { Length: > 0 })
                scrollback.Load(history);

            var terminal = new LiveTerminal(
                context,
                id,
                title ?? NextTitle(shell.Name, existing),
                createdAt,
                process,
                scrollback,
                store,
                logger,
                OnExitedAsync);
            _live[(context.SessionId, id)] = terminal;
            terminal.Start();
            return TerminalResult.Ok(terminal);
        }

        return TerminalResult.Fail<LiveTerminal>(TerminalErrorKind.SpawnFailed,
            candidates.Count == 0
                ? "No shell was found to start. Set SHELL (or install bash)."
                : $"No shell could be started. Tried {string.Join(", ", candidates.Select(c => c.Path))}.");
    }

    /// <summary>
    /// A shell that ends on its own closes its tab, the way closing a terminal window does. Called by the
    /// terminal's own read loop, which releases the terminal afterwards.
    /// </summary>
    private async Task OnExitedAsync(LiveTerminal terminal, PtyExit exit)
    {
        var key = (terminal.Context.SessionId, terminal.Id);
        if (!_live.TryRemove(new KeyValuePair<(string, string), LiveTerminal>(key, terminal)))
            return;

        await store.RemoveTerminalAsync(terminal.Context.SessionId, terminal.Id).ConfigureAwait(false);
        await BroadcastAsync(terminal.Context, "terminal.closed", terminal.Id, terminal.Title, exit.ExitCode, CancellationToken.None).ConfigureAwait(false);
    }

    internal static string NextTitle(string shellName, IReadOnlyList<SavedTerminal> existing)
    {
        var taken = existing.Select(t => t.Title).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(shellName))
            return shellName;
        for (var n = 2; ; n++)
        {
            var title = $"{shellName} {n}";
            if (!taken.Contains(title))
                return title;
        }
    }

    private Task BroadcastAsync(TerminalContext context, string type, string terminalId, string title, int? exitCode, CancellationToken ct)
    {
        var payload = new TerminalPayload
        {
            SessionId = context.SessionId,
            TerminalId = terminalId,
            Title = title,
            ExitCode = exitCode,
        };
        DomainEvent domainEvent = type == "terminal.opened"
            ? new TerminalOpened { Payload = payload }
            : new TerminalClosed { Payload = payload };

        return events.BroadcastAsync(
            $"session:{context.SessionId}",
            type,
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.TerminalPayload),
            domainEvent,
            context.UserId,
            ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting shell {Shell} for a terminal failed")]
    private static partial void LogSpawnFailed(ILogger logger, Exception ex, string shell);
}
