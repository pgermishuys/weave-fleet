using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>Where a server sends the events of one V2 session.</summary>
internal interface IOpenCode2EventSink
{
    /// <summary>The Fleet session the events are for.</summary>
    OpenCode2SessionContext Context { get; }

    void OnEvent(OpenCode2Event evt);

    /// <summary>
    /// The event stream reconnected after a drop: catch up on what was missed. <paramref name="activeSessions"/> are
    /// the sessions with a turn running now. Runs on the event pump before any newer event is delivered.
    /// </summary>
    Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct);

    /// <summary>The server stopped; nothing more will arrive from it.</summary>
    void OnServerStopped();
}

/// <summary>
/// What Fleet starts an owner's server with: where Fleet is, the config it adds (<see cref="OpenCode2FleetFiles"/>),
/// whether the server gets the tools for messages between sessions and workflow steps, the install it runs (<see cref="OpenCode2Install"/>),
/// and the profile its sessions use, if any (<see cref="OpenCode2Profiles"/>).
/// When the owner changes a setting behind it, the server is replaced once nothing runs on it (no turn, no background
/// shell).
/// </summary>
internal sealed record OpenCode2ServerSetup(
    string? FleetUrl,
    string? ConfigContent,
    bool SessionMessages,
    string? ExecutablePath = null,
    OpenCode2InstallMode Mode = OpenCode2InstallMode.Default,
    OpenCode2Profile? Profile = null,
    bool Workflows = false)
{
    public static readonly OpenCode2ServerSetup None = new(null, null, false);
}

/// <summary>
/// One <c>opencode2 serve</c> process that Fleet runs for one owner, serving every directory: V2 takes the directory
/// per session (<c>location</c>), so there's no process per folder. Its single event stream carries every session's
/// events and is routed to the attached sessions by <c>sessionID</c>. A subagent's child session starts before Fleet
/// has a session for it, so its events are held from its <c>session.created</c> until Fleet attaches it. Events for
/// other sessions nobody attached are dropped. The stream is live only, so when it reconnects the attached sessions
/// read what they missed.
/// </summary>
internal sealed partial class OpenCode2Server : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long a child session's events are held for Fleet to attach it, and how many.</summary>
    internal static readonly TimeSpan PendingChildLifetime = TimeSpan.FromMinutes(10);
    internal const int PendingChildEventLimit = 5000;

    /// <summary>
    /// The catalog events V2 sends for a folder once it has read the folder's config. Until then its agents, models
    /// and commands leave out the user's and the folder's own.
    /// </summary>
    private static readonly string[] LocationLoadedEvents = ["provider.updated", "model.updated", "agent.updated", "command.updated"];

    /// <summary>
    /// The events that mean a loaded folder's agents, models or commands may have changed. V2 rebuilds a registry
    /// whenever a file it watches changes (an agent file, the config, a sign-in) and says which kind and where, never
    /// what; a change in the shared config folder comes once per loaded folder.
    /// </summary>
    private static readonly string[] CatalogChangeEvents = ["agent.updated", "model.updated", "provider.updated", "command.updated", "config.updated"];

    private readonly OpenCode2ProcessManager? _process;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IOpenCode2EventSink> _sinks = new(StringComparer.Ordinal);

    // Child sessions of attached sessions (or of other held children) that Fleet hasn't attached yet, with their
    // events so far. Routing and attaching take _routing, so a child's held events reach it before any newer one.
    private readonly Dictionary<string, PendingChild> _pending = new(StringComparer.Ordinal);
    private readonly object _routing = new();

    // Folders V2 has finished loading, and the catalog events seen so far for folders still loading.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _locations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _locationEvents = new(StringComparer.Ordinal);

    // When each folder finished loading (Environment.TickCount64), the folders Fleet asked V2 about, and the folders
    // whose catalog changed and is waiting for V2 to go quiet, with the time of the last event.
    private readonly ConcurrentDictionary<string, long> _loadedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _catalogChanges = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private TaskCompletionSource _connected = NewConnectedSource();
    private int _stopped;
    private int _outdated;
    private long _lastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;

    internal OpenCode2Server(
        string ownerUserId,
        OpenCode2HttpClient client,
        string bridgeToken,
        OpenCode2ProcessManager? process,
        ILogger logger,
        OpenCode2ServerSetup? setup = null)
    {
        OwnerUserId = ownerUserId;
        Client = client;
        BridgeToken = bridgeToken;
        Setup = setup ?? OpenCode2ServerSetup.None;
        _process = process;
        _logger = logger;

        if (_process is not null)
            _process.Exited += (_, _) => _ = StopRoutingAsync();

        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public string OwnerUserId { get; }

    public OpenCode2HttpClient Client { get; }

    /// <summary>Set as <c>FLEET_BRIDGE_TOKEN</c> in the process, and in <c>FLEET_URL</c>, so Fleet knows its calls.</summary>
    public string BridgeToken { get; }

    public OpenCode2ServerSetup Setup { get; }

    /// <summary>The profile this server's sessions use; <see langword="null"/> for the owner's server without one.</summary>
    public OpenCode2Profile? Profile => Setup.Profile;

    public int? ProcessId => _process?.ProcessId;

    /// <summary>When a session last asked something of this server, or it last sent an event.</summary>
    public DateTimeOffset LastUsed => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

    /// <summary>Notes that the server is in use now, so it isn't stopped as idle.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>Whether Fleet session <paramref name="fleetSessionId"/> listens on this server.</summary>
    public bool Serves(string fleetSessionId)
        => _sinks.Values.Any(sink => string.Equals(sink.Context.FleetSessionId, fleetSessionId, StringComparison.Ordinal));

    /// <summary>How long a catalog read waits for V2 to load a folder before it lists what V2 has so far.</summary>
    internal TimeSpan LocationLoadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Told when what V2 offers in a folder Fleet uses changed (<see cref="CatalogChangeEvents"/>), once V2 has been
    /// quiet about it for <see cref="CatalogChangeQuietTime"/>: one file written sends two or three rounds of events.
    /// Events from a folder's first <see cref="LocationSettleTime"/> after it loaded are its loading, not a change.
    /// </summary>
    internal Action<OpenCode2Server, string>? CatalogChanged { get; init; }

    internal TimeSpan CatalogChangeQuietTime { get; init; } = TimeSpan.FromSeconds(1);

    internal TimeSpan LocationSettleTime { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Loads <paramref name="directory"/> on the server and waits until V2 has read its config. V2 loads a folder in
    /// the background: <c>GET /api/location</c> returns first, and the folder's catalog is complete once V2 has sent
    /// its catalog events for it (about 2 s for a new folder). A folder loaded before (by a session there, or an
    /// earlier read) isn't waited for again.
    /// </summary>
    public async Task LoadLocationAsync(string directory, CancellationToken ct)
    {
        await WaitForEventsAsync(ct).ConfigureAwait(false);
        _asked.TryAdd(Path.TrimEndingDirectorySeparator(directory), 0);
        var loaded = _locations.GetOrAdd(directory, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (loaded.Task.IsCompleted)
            return;

        await Client.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        try
        {
            await loaded.Task.WaitAsync(LocationLoadTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogLocationLoadTimedOut(_logger, directory, LocationLoadTimeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Notes a folder's catalog events; once V2 has sent them all, the folder counts as loaded. Returns whether this
    /// event finished loading it.
    /// </summary>
    private bool ObserveLocation(OpenCode2Event evt)
    {
        if (evt.Location?.Directory is not { Length: > 0 } directory || Array.IndexOf(LocationLoadedEvents, evt.Type) < 0)
            return false;

        var seen = _locationEvents.GetOrAdd(directory, static _ => new HashSet<string>(StringComparer.Ordinal));
        lock (seen)
        {
            seen.Add(evt.Type);
            if (seen.Count < LocationLoadedEvents.Length)
                return false;
            seen.Clear();
        }

        if (!_locations.GetOrAdd(directory, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult())
            return false;

        _loadedAt.TryAdd(directory, Environment.TickCount64);
        return true;
    }

    /// <summary>
    /// Notes a change to a loaded folder's catalog, and tells <see cref="CatalogChanged"/> once V2 is quiet about it.
    /// Only folders Fleet uses count: V2 also loads its own working folder, which no session runs in.
    /// </summary>
    private void ObserveCatalogChange(OpenCode2Event evt)
    {
        if (CatalogChanged is null
            || evt.Location?.Directory is not { Length: > 0 } directory
            || Array.IndexOf(CatalogChangeEvents, evt.Type) < 0
            || !_loadedAt.TryGetValue(directory, out var loadedAt))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - loadedAt < (long)LocationSettleTime.TotalMilliseconds || !Uses(directory))
            return;

        lock (_catalogChanges)
        {
            var waiting = _catalogChanges.ContainsKey(directory);
            _catalogChanges[directory] = now;
            if (waiting)
                return;
        }

        _ = ReportCatalogChangeAsync(directory);
    }

    /// <summary>Whether Fleet asked V2 about <paramref name="directory"/> or runs a session there.</summary>
    private bool Uses(string directory)
    {
        var folder = Path.TrimEndingDirectorySeparator(directory);
        return _asked.ContainsKey(folder) || _sinks.Values.Any(sink => SameFolder(sink.Context.WorkingDirectory, folder));
    }

    private async Task ReportCatalogChangeAsync(string directory)
    {
        try
        {
            var quiet = (long)CatalogChangeQuietTime.TotalMilliseconds;
            while (true)
            {
                long wait;
                lock (_catalogChanges)
                {
                    wait = _catalogChanges[directory] + quiet - Environment.TickCount64;
                    if (wait <= 0)
                    {
                        _catalogChanges.Remove(directory);
                        break;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(wait), _stopping.Token).ConfigureAwait(false);
            }

            LogCatalogChanged(_logger, ProcessId ?? 0, directory);
            CatalogChanged?.Invoke(this, directory);
        }
        catch (OperationCanceledException)
        {
            // The server stopped; its sessions will ask a new one.
        }
        catch (Exception ex)
        {
            LogSinkFailed(_logger, "catalog change", ex);
        }
    }

    /// <summary>The Fleet sessions on this server that run in <paramref name="directory"/>.</summary>
    public IReadOnlyList<string> SessionsIn(string directory)
    {
        var folder = Path.TrimEndingDirectorySeparator(directory);
        return _sinks.Values
            .Where(sink => SameFolder(sink.Context.WorkingDirectory, folder))
            .Select(sink => sink.Context.FleetSessionId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool SameFolder(string directory, string folder)
        => string.Equals(Path.TrimEndingDirectorySeparator(directory), folder, StringComparison.Ordinal);

    /// <summary>Set after V2 was updated: the server still runs the old binary, and is replaced once it's idle.</summary>
    public bool IsOutdated => Volatile.Read(ref _outdated) == 1;

    public void MarkOutdated() => Volatile.Write(ref _outdated, 1);

    /// <summary>False once the process exited or Fleet stopped it; a stopped server is replaced, not restarted.</summary>
    public bool IsRunning => Volatile.Read(ref _stopped) == 0 && _process?.IsRunning != false;

    /// <summary>Completes when the event stream is open, so events that follow a request aren't missed.</summary>
    public Task WaitForEventsAsync(CancellationToken ct) => Volatile.Read(ref _connected).Task.WaitAsync(ct);

    /// <summary>Routes the session's events to <paramref name="sink"/>, starting with any held while it was a new child session.</summary>
    public void Attach(string harnessSessionId, IOpenCode2EventSink sink)
    {
        lock (_routing)
        {
            _sinks[harnessSessionId] = sink;
            if (_pending.Remove(harnessSessionId, out var held))
            {
                LogChildAttached(_logger, harnessSessionId, held.Events.Count);
                foreach (var evt in held.Events)
                    Deliver(sink, evt);
            }
        }
    }

    public void Detach(string harnessSessionId, IOpenCode2EventSink sink)
    {
        lock (_routing)
            _sinks.TryRemove(new KeyValuePair<string, IOpenCode2EventSink>(harnessSessionId, sink));
    }

    /// <summary>Whether events for <paramref name="harnessSessionId"/> are being held for Fleet to attach it.</summary>
    internal bool IsHolding(string harnessSessionId)
    {
        lock (_routing)
            return _pending.ContainsKey(harnessSessionId);
    }

    /// <summary>
    /// Delivers one event from the stream: to its session when attached, held when it belongs to a child session
    /// Fleet will attach, otherwise dropped (a permission ask is answered, since nothing else would).
    /// </summary>
    internal void Route(OpenCode2Event evt)
    {
        if (!ObserveLocation(evt))
            ObserveCatalogChange(evt);
        if (evt.SessionId is not { } sessionId)
            return;

        // Only a session's own events count as use: V2 also sends catalog events whenever files it watches change.
        Touch();

        IOpenCode2EventSink? sink;
        lock (_routing)
        {
            if (!_sinks.TryGetValue(sessionId, out sink))
            {
                if (evt.Type == "session.created" && ParentId(evt) is { } parentId
                    && (_sinks.ContainsKey(parentId) || _pending.ContainsKey(parentId)))
                {
                    ForgetExpiredChildren();
                    _pending[sessionId] = new PendingChild(DateTimeOffset.UtcNow);
                    LogChildHeld(_logger, sessionId, parentId);
                }

                // Asks are answered now: the child's turn, and its parent's, wait on them.
                if (evt.Type != "permission.asked" && _pending.TryGetValue(sessionId, out var held))
                {
                    if (held.Events.Count < PendingChildEventLimit)
                        held.Events.Add(evt);
                    return;
                }
            }
        }

        if (sink is not null)
            Deliver(sink, evt);
        else if (evt.Type == "permission.asked")
            AllowOnce(sessionId, evt);
    }

    private void ForgetExpiredChildren()
    {
        var expired = DateTimeOffset.UtcNow - PendingChildLifetime;
        foreach (var (sessionId, held) in _pending.Where(p => p.Value.Since < expired).ToList())
        {
            _pending.Remove(sessionId);
            LogChildDropped(_logger, sessionId, held.Events.Count);
        }
    }

    private static string? ParentId(OpenCode2Event evt)
        => evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && evt.Data.TryGetProperty("parentID", out var parent)
            && parent.ValueKind == System.Text.Json.JsonValueKind.String
                ? parent.GetString()
                : null;

    /// <summary>The Fleet session attached to V2 session <paramref name="harnessSessionId"/> on this server, if any.</summary>
    public OpenCode2SessionContext? FindSession(string harnessSessionId)
        => _sinks.TryGetValue(harnessSessionId, out var sink) ? sink.Context : null;

    /// <summary>
    /// Whether nothing runs on this server: no session is running a turn and no shell command is running in any folder
    /// it has loaded; <see langword="false"/> when V2 can't say. A shell call moved to the background keeps running
    /// after its turn ended, and V2 no longer counts its session as active, so stopping the server then would kill the
    /// command and lose the notice V2 posts when it finishes. A background subagent's child session counts as active
    /// while it works.
    /// </summary>
    public async Task<bool> IsIdleAsync(CancellationToken ct)
    {
        try
        {
            if ((await Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false)).Count > 0)
                return false;

            // V2 lists one folder's shells at a time, and asking about a folder it hasn't loaded would load it.
            foreach (var directory in await Client.GetLoadedLocationsAsync(ct).ConfigureAwait(false))
            {
                var running = (await Client.GetRunningShellsAsync(directory, ct).ConfigureAwait(false))
                    .Count(shell => string.Equals(shell.Status, "running", StringComparison.Ordinal));
                if (running > 0)
                {
                    LogShellsRunning(_logger, ProcessId ?? 0, running, directory);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    private async Task PumpAsync()
    {
        var ct = _stopping.Token;
        var reconnecting = false;
        while (!ct.IsCancellationRequested && IsRunning)
        {
            try
            {
                var resync = reconnecting;
                await foreach (var evt in Client.ReadEventsAsync(OnConnected, ct).ConfigureAwait(false))
                {
                    // V2 opens every stream with server.connected, so this runs as soon as the stream is back.
                    if (resync)
                    {
                        resync = false;
                        await ResyncAsync(ct).ConfigureAwait(false);
                    }

                    Route(evt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogStreamFailed(_logger, ProcessId ?? 0, ex);
            }

            // The stream ended with the process still up: open it again, and make new waiters wait for that.
            // Anything sent in between is lost (V2 doesn't replay), so the sessions catch up once it's back.
            if (Volatile.Read(ref _connected).Task.IsCompleted)
            {
                Volatile.Write(ref _connected, NewConnectedSource());
                reconnecting = true;
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A permission ask from a session no Fleet session listens to (a subagent's child session, which doesn't get
    /// Fleet's allow-all rules) would wait forever, and so would the turn that started it. Every session on this
    /// server is Fleet's, so it's allowed once, like an attached session's.
    /// </summary>
    private void AllowOnce(string sessionId, OpenCode2Event evt)
    {
        if (!evt.Data.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
            return;

        var requestId = id.GetString()!;
        LogPermissionAllowed(_logger, sessionId, requestId);
        _ = ReplyAsync();

        async Task ReplyAsync()
        {
            try
            {
                await Client.ReplyToPermissionAsync(sessionId, requestId, "once", _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                LogPermissionReplyFailed(_logger, sessionId, requestId, ex);
            }
        }
    }

    private async Task ResyncAsync(CancellationToken ct)
    {
        if (_sinks.IsEmpty)
            return;

        try
        {
            var active = await Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false);
            foreach (var sink in _sinks.Values)
            {
                try
                {
                    await sink.ResyncAsync(active, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    LogSinkFailed(_logger, "resync", ex);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogResyncFailed(_logger, ProcessId ?? 0, ex);
        }
    }

    private void OnConnected()
    {
        LogConnected(_logger, ProcessId ?? 0);
        Volatile.Read(ref _connected).TrySetResult();
    }

    private void Deliver(IOpenCode2EventSink sink, OpenCode2Event evt)
    {
        try
        {
            sink.OnEvent(evt);
        }
        catch (Exception ex)
        {
            // One session's bad event must not stop the stream for every other session.
            LogSinkFailed(_logger, evt.Type, ex);
        }
    }

    /// <summary>Stops routing and tells every attached session the server is gone.</summary>
    private async Task StopRoutingAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        await _stopping.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _connected).TrySetException(new InvalidOperationException("The OpenCode 2 server stopped."));
        foreach (var sink in _sinks.Values)
        {
            try
            {
                sink.OnServerStopped();
            }
            catch (Exception ex)
            {
                LogSinkFailed(_logger, "server stopped", ex);
            }
        }
        lock (_routing)
        {
            _sinks.Clear();
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopRoutingAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        if (_process is not null)
            await _process.DisposeAsync().ConfigureAwait(false);
        Client.Dispose();
        _stopping.Dispose();
    }

    private static TaskCompletionSource NewConnectedSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PendingChild(DateTimeOffset Since)
    {
        public List<OpenCode2Event> Events { get; } = [];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 didn't finish loading {Directory} within {Seconds} s; listing what it has")]
    private static partial void LogLocationLoadTimedOut(ILogger logger, string directory, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId}: the agents, models or commands in {Directory} changed")]
    private static partial void LogCatalogChanged(ILogger logger, int processId, string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 session {HarnessSessionId} is a child of {ParentSessionId}; holding its events until Fleet attaches it")]
    private static partial void LogChildHeld(ILogger logger, string harnessSessionId, string parentSessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 child session {HarnessSessionId} attached; delivering {Count} held event(s)")]
    private static partial void LogChildAttached(ILogger logger, string harnessSessionId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 child session {HarnessSessionId} was never attached; dropped {Count} held event(s)")]
    private static partial void LogChildDropped(ILogger logger, string harnessSessionId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 server {ProcessId}: no turn running, but {Count} shell command(s) still running in {Directory}; not idle")]
    private static partial void LogShellsRunning(ILogger logger, int processId, int count, string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 server {ProcessId}: event stream open")]
    private static partial void LogConnected(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 server {ProcessId}: event stream failed; reconnecting")]
    private static partial void LogStreamFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 session {HarnessSessionId} (not a Fleet session's own, e.g. a subagent's) asked permission ({RequestId}); allowed once, since nobody can answer it")]
    private static partial void LogPermissionAllowed(ILogger logger, string harnessSessionId, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't answer OpenCode 2 permission request {RequestId} for session {HarnessSessionId}")]
    private static partial void LogPermissionReplyFailed(ILogger logger, string harnessSessionId, string requestId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 server {ProcessId}: couldn't read the running sessions after the event stream reconnected")]
    private static partial void LogResyncFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An OpenCode 2 session failed to handle {EventType}")]
    private static partial void LogSinkFailed(ILogger logger, string eventType, Exception exception);
}
