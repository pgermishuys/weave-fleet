using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>Finds a session as its user, outside any request; null when there's none.</summary>
internal delegate Task<Session?> ModSessionLookup(string userId, string sessionId, CancellationToken ct);

/// <summary>What a <see cref="ModHostSupervisor"/> needs from the rest of Fleet.</summary>
internal sealed record ModHostDependencies(
    ModHostOptions Options,
    IModHostConnectionFactory Connections,
    IModUserGate Gate,
    IModHostBun Bun,
    IModHostFiles Files,
    IModVersionStore Store,
    IModStrikeRecorder Strikes,
    IModHostUi Ui,
    ModSessionLookup FindSession,
    TimeProvider Time,
    ILogger Logger);

/// <summary>
/// One user's mod host: keeps the process running while a mod of theirs should run, stops it when none should, and starts
/// it again, after a growing wait, when it dies or stops answering. It loads the user's kept mods and drafts, routes events
/// to them and answers their calls. Everything that changes the process, or what it has loaded, runs under one gate.
/// </summary>
#pragma warning disable CA1001 // The gate is never waited on by handle, so there is nothing to release.
internal sealed partial class ModHostSupervisor(string userId, ModHostDependencies deps) : IModHostCalls
{
    private static readonly HashSet<string> Calls = ["store.get", "store.set", "store.delete", "store.keys", "session.get", "ui.open", "ui.close", "ui.toast"];
    private static readonly JsonElement Empty = Params(_ => { });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16].ToLowerInvariant();
    private readonly ILogger _logger = deps.Logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, LoadedMod> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ModLoadProblem> _problems = new(StringComparer.Ordinal);
    private IReadOnlyList<ModRoute> _routes = [];
    private TaskCompletionSource? _loading;
    private ModHostStatus _status = ModHostStatus.Stopped;
    private IModHostConnection? _connection;
    private IModHostConnection? _killed;
    private ITimer? _restartTimer;
    private ITimer? _leaseTimer;
    private DateTimeOffset _upSince;
    private DateTimeOffset _checkUntil;
    private int _step;
    private int _restarts;
    private int _generation;
    private bool _shutdown;

    private ModHostOptions Options => deps.Options;

    /// <summary>Drafts are staged for the host in <c>{host}/drafts/{session}/{generation}/{name}</c>.</summary>
    private string StagedRoot => Path.Combine(deps.Store.HostFolder(userId), "drafts");

    /// <summary>Raised on every change of <see cref="GetStatus"/>, from whichever thread made it.</summary>
    internal event Action<ModHostStatus>? Changed;

    public ModHostStatus GetStatus() => Volatile.Read(ref _status);

    public ModLoadProblem? GetLoadProblem(string modId) => _problems.GetValueOrDefault(modId);

    /// <summary>The user's mods changed: every refused mod gets another try at the next ensure.</summary>
    public void ClearLoadProblems() => _problems.Clear();

    /// <summary>Brings the host in line with the Mods switch, safe mode and what the user has kept or drafted.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_shutdown)
                await ReconcileAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fleet is stopping: the host gets its grace to exit and nothing starts it again.</summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _shutdown = true;
            _leaseTimer?.Dispose();
            await StopHostAsync().ConfigureAwait(false);
            Set(ModHostStates.Stopped, "Fleet is stopping.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one event through its chain; nothing crosses when no loaded mod hooks it, unless it's a control event. A host
    /// that just started holds it until it has loaded and sent each session's reload start.
    /// </summary>
    public async Task<ModDispatchResult> DispatchAsync(ModDispatchRequest request, CancellationToken ct)
    {
        if (Volatile.Read(ref _connection) is null)
            return ModDispatchResult.NotDispatched;
        if (Volatile.Read(ref _loading) is { } loading)
            await loading.Task.WaitAsync(ct).ConfigureAwait(false);
        var chain = ModRouting.ChainFor(Volatile.Read(ref _routes), request.Event, request.SessionId, request.E);
        if (chain.Count == 0 && request.Event is not ("ui.press" or "ui.input" or "ui.select"))
            return ModDispatchResult.NotDispatched;
        lock (_sync)
            _seen.Add(request.SessionId);
        try
        {
            return await SendDispatchAsync(request, chain).ConfigureAwait(false);
        }
        catch (Exception ex) when (Gone(ex) || ex is ModHostRpcException)
        {
            return ModDispatchResult.NotDispatched;
        }
    }

    /// <summary>The static check of a folder. Starts a host for it that stays up for checks a while but loads no mod.</summary>
    public async Task<JsonElement> CheckAsync(string folder, CancellationToken ct)
    {
        if (!await deps.Gate.IsSwitchedOnAsync(userId, ct).ConfigureAwait(false))
            throw new ModHostNotReadyException(ModsFeature.TurnedOffMessage);
        lock (_sync)
        {
            _checkUntil = deps.Time.GetUtcNow() + Options.CheckLease;
            _leaseTimer ??= deps.Time.CreateTimer(_ => _ = InBackgroundAsync(() => EnsureAsync(CancellationToken.None)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _leaseTimer.Change(Options.CheckLease, Timeout.InfiniteTimeSpan);
        }
        await EnsureAsync(ct).ConfigureAwait(false);
        try
        {
            return await RequestAsync("check", Params(w =>
            {
                w.WriteString("root", folder);
                w.WriteString("manifest", Path.Combine(folder, "mod.json"));
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ModHostRpcException or ModHostClosedException)
        {
            throw new ModHostNotReadyException(ex.Message);
        }
    }

    /// <summary>
    /// Stages the draft again and loads it over the running one; the old copy goes once the host took the new one. A draft
    /// that isn't loaded (refused before, or new) is loaded if it should be.
    /// </summary>
    public async Task ReloadDraftAsync(string sessionId, string name, CancellationToken ct)
    {
        var id = $"{name}@draft:{sessionId}";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shutdown)
                return;
            var wasLoaded = IsLoaded(id);
            _problems.TryRemove(id, out _);
            await ReconcileAsync(ct).ConfigureAwait(false);
            if (wasLoaded && IsLoaded(id))
                await LoadOneAsync(new WantedMod(id, name, null, sessionId), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Gone(ex))
        {
            // It hung or died: it's restarted, and stages the draft afresh then.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The host drops the session's state, timers and handles.</summary>
    public async Task ForgetSessionAsync(string sessionId)
    {
        lock (_sync)
            _seen.Remove(sessionId);
        try
        {
            if (Volatile.Read(ref _connection) is not null)
                await RequestAsync("forget", Params(w => w.WriteString("sessionId", sessionId))).ConfigureAwait(false);
        }
        catch (Exception ex) when (Gone(ex) || ex is ModHostRpcException)
        {
        }
    }

    /// <summary>Sends the host a request. A host that doesn't answer in time is killed and restarted, and the caller told so.</summary>
    internal async Task<JsonElement> RequestAsync(string method, JsonElement parameters)
    {
        var connection = Volatile.Read(ref _connection)
            ?? throw new ModHostNotReadyException(GetStatus().Reason ?? "The mod host isn't running.");
        try
        {
            return await connection.RequestAsync(method, parameters, Options.RequestTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!ReferenceEquals(Interlocked.Exchange(ref _killed, connection), connection))
            {
                LogRequestTimedOut(_userKey, method);
                connection.Kill();
            }
            throw new ModHostNotReadyException($"The mod host didn't answer within {Options.RequestTimeout.TotalSeconds:0.#} s; Fleet is restarting it.");
        }
        catch (ModHostClosedException)
        {
            // The process is gone: its exit takes the crash path, which restarts it.
            throw new ModHostNotReadyException("The mod host stopped; Fleet is restarting it.");
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var (reason, mods) = await WantedAsync(ct).ConfigureAwait(false);
        if (reason is not null)
        {
            await StopHostAsync().ConfigureAwait(false);
            Set(ModHostStates.Stopped, reason);
            return;
        }
        if (_connection is not null || _restartTimer is not null)
        {
            await LoadAsync(mods, fresh: false, ct).ConfigureAwait(false);
            return;
        }
        if (deps.Files.HostScript is not { } script)
        {
            Set(ModHostStates.NotReady, "Fleet can't find the mod host (mods-host/host.js).");
            return;
        }
        if (await deps.Bun.FindAsync(ct).ConfigureAwait(false) is not { } bun)
        {
            Set(ModHostStates.NotReady, "The mod runtime (Bun) isn't installed yet.");
            return;
        }

        Set(ModHostStates.Starting, null);
        try
        {
            DeleteFolder(StagedRoot);
            var launch = new ModHostLaunch(bun.ExecutablePath, script, deps.Store.HostFolder(userId), deps.Files.FleetVersion, _userKey);
            var connection = await deps.Connections.StartAsync(launch, this, ct).ConfigureAwait(false);
            // Set before anyone can see the connection: dispatches wait for this run's loads and reload starts.
            Volatile.Write(ref _loading, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _connection, connection);
            _upSince = deps.Time.GetUtcNow();
            Set(ModHostStates.Running, null, connection, bun.ExecutablePath);
            _ = WatchAsync(connection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not ModHostNotReadyException)
                LogFailed(ex, _userKey);
            Set(ModHostStates.NotReady, ex is ModHostNotReadyException ? ex.Message : "The mod host couldn't start.");
            return;
        }
        await LoadAsync(mods, fresh: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why no host is wanted (the sentence for the user), or null when one is; and the mods it should run. A host wanted
    /// only for checks (safe mode, or nothing kept or drafted) runs none.
    /// </summary>
    private async Task<(string? Reason, List<WantedMod> Mods)> WantedAsync(CancellationToken ct)
    {
        if (!await deps.Gate.IsSwitchedOnAsync(userId, ct).ConfigureAwait(false))
            return ("Mods are off.", []);
        bool forChecks;
        lock (_sync)
            forChecks = deps.Time.GetUtcNow() < _checkUntil;
        if (deps.Gate.IsSafeMode(userId))
            return (forChecks ? null : "Started without mods.", []);
        var mods = (await deps.Store.ListAsync(userId, ct).ConfigureAwait(false))
            .Where(h => h is { Active: not null, Off: null })
            .Select(h => new WantedMod($"{h.Name}@v{h.Active}", h.Name, h.Active, null))
            .ToList();
        foreach (var session in await deps.Store.ListDraftSessionsAsync(userId, ct).ConfigureAwait(false))
        {
            mods.AddRange((await deps.Store.ListDraftsAsync(userId, session, ct).ConfigureAwait(false))
                .Where(d => d is { Off: null, Manifest: not null })
                .Select(d => new WantedMod($"{d.Name}@draft:{session}", d.Name, null, session)));
        }
        return (mods.Count > 0 || forChecks ? null : "No mod is kept or drafted.", mods);
    }

    /// <summary>
    /// Unloads what's no longer wanted, then loads what isn't loaded (a refused mod waits for the next change); on a
    /// <paramref name="fresh"/> host, then sends the reload starts and lets dispatches through.
    /// </summary>
    private async Task LoadAsync(List<WantedMod> mods, bool fresh, CancellationToken ct)
    {
        try
        {
            string[] gone;
            lock (_sync)
                gone = [.. _loaded.Keys.Where(id => mods.TrueForAll(m => m.Id != id))];
            foreach (var id in gone)
            {
                await Quietly(RequestAsync("unload", Params(w => w.WriteString("id", id)))).ConfigureAwait(false);
                Drop(id);
            }
            foreach (var mod in mods.Where(m => _connection is not null && !IsLoaded(m.Id) && !_problems.ContainsKey(m.Id)))
                await LoadOneAsync(mod, ct).ConfigureAwait(false);
            if (fresh)
                await SendReloadStartsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (Gone(ex))
        {
            // It hung or died while loading: it's restarted, and loads everything again then.
        }
        finally
        {
            if (fresh)
                _loading?.TrySetResult();
        }
    }

    /// <summary>Loads (or reloads) one mod, a draft from a fresh staged copy. A refusal becomes its load problem.</summary>
    private async Task LoadOneAsync(WantedMod mod, CancellationToken ct)
    {
        var staged = mod.SessionId is { } session ? Path.Combine(StagedRoot, session, (++_generation).ToString(CultureInfo.InvariantCulture), mod.Name) : null;
        var took = false;
        try
        {
            if (staged is not null)
                await deps.Store.StageDraftAsync(userId, mod.SessionId!, mod.Name, staged, ct).ConfigureAwait(false);
            var answer = await RequestAsync("load", Params(w =>
            {
                w.WriteString("id", mod.Id);
                w.WriteString("name", mod.Name);
                if (mod.Number is { } number)
                    w.WriteNumber("version", number);
                else
                    w.WriteString("version", "draft");
                if (mod.SessionId is { } draftSession)
                    w.WriteString("sessionId", draftSession);
                w.WriteString("root", staged ?? deps.Store.VersionFolder(userId, mod.Name, mod.Number!.Value));
            })).ConfigureAwait(false);
            LoadedMod? old;
            lock (_sync)
            {
                _loaded.Remove(mod.Id, out old);
                _loaded[mod.Id] = new LoadedMod(new ModRoute(mod.Id, mod.Name, mod.SessionId, HooksOf(answer)), staged);
                Publish();
            }
            took = true;
            _problems.TryRemove(mod.Id, out _);
            DeleteStaged(old?.Staged);
        }
        catch (Exception ex) when (ex is ModHostRpcException or ModStoreException)
        {
            _problems[mod.Id] = new ModLoadProblem(ex.Message, (ex as ModHostRpcException)?.Data);
        }
        finally
        {
            if (!took)
                DeleteStaged(staged);
        }
    }

    /// <summary>After a start, each session that had events gets <c>session.start</c> with <c>reason: "reload"</c>.</summary>
    private async Task SendReloadStartsAsync()
    {
        string[] seen;
        lock (_sync)
            seen = [.. _seen];
        foreach (var session in seen)
        {
            var e = Params(w =>
            {
                w.WriteString("sessionId", session);
                w.WriteString("reason", "reload");
            });
            if (ModRouting.ChainFor(Volatile.Read(ref _routes), "session.start", session, e) is { Count: > 0 } chain)
                await Quietly(SendDispatchAsync(new ModDispatchRequest("session.start", session, e), chain)).ConfigureAwait(false);
        }
    }

    private async Task<ModDispatchResult> SendDispatchAsync(ModDispatchRequest request, IReadOnlyList<string> chain)
    {
        var answer = await RequestAsync("dispatch", Params(w =>
        {
            w.WriteString("event", request.Event);
            w.WriteString("sessionId", request.SessionId);
            w.WritePropertyName("e");
            request.E.WriteTo(w);
            Strings(w, "mods", chain);
            if (request.Surface is { } surface)
                w.WriteString("surface", surface);
        })).ConfigureAwait(false);
        if (answer.ValueKind != JsonValueKind.Object)
            return new ModDispatchResult(true, null, []);
        foreach (var failure in Items(answer, "failures"))
            Strike(failure);
        return new ModDispatchResult(true, answer.TryGetProperty("result", out var result) ? result.Clone() : null,
            [.. Items(answer, "drawnBy").Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!)]);
    }

    /// <summary>
    /// A failure the host counted: at three strikes for a mod loaded now, it leaves routing and is turned off, once. The
    /// recorder raises <c>mods.changed</c>, which reconciles, so it's never awaited here (this may run under the gate).
    /// </summary>
    private void Strike(JsonElement failure)
    {
        if (Text(failure, "mod") is not { } id
            || !failure.TryGetProperty("strikes", out var count) || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt32(out var strikes) || strikes < 3 || Drop(id) is not { Route: var route })
        {
            return;
        }
        var message = Text(failure, "message") ?? "It failed three times in a row.";
        // Not loaded again until the recorder's mods.changed clears this.
        _problems[id] = new ModLoadProblem(message, null);
        _ = InBackgroundAsync(() => route.DraftSessionId is { } session
            ? deps.Strikes.RecordDraftAsync(userId, session, route.Name, message, CancellationToken.None)
            : deps.Strikes.RecordKeptAsync(userId, route.Name, message, CancellationToken.None));
    }

    private bool IsLoaded(string id)
    {
        lock (_sync)
            return _loaded.ContainsKey(id);
    }

    /// <summary>Takes a mod (or, for null, every mod) out of routing and deletes its staged copy; null when it wasn't loaded.</summary>
    private LoadedMod? Drop(string? id)
    {
        LoadedMod? dropped = null;
        lock (_sync)
        {
            if (id is null)
                _loaded.Clear();
            else if (!_loaded.Remove(id, out dropped))
                return null;
            Publish();
        }
        if (id is null)
            DeleteFolder(StagedRoot);
        DeleteStaged(dropped?.Staged);
        return dropped;
    }

    private void Publish() => Volatile.Write(ref _routes, [.. _loaded.Values.Select(l => l.Route)]);

    private async Task InBackgroundAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailed(ex, _userKey);
        }
    }

    /// <summary>Waits for the process to exit; if Fleet didn't ask it to (it still holds the connection), plans a restart.</summary>
    private async Task WatchAsync(IModHostConnection connection)
    {
        var code = await connection.Exited.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : -1, TaskScheduler.Default).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_connection, connection))
                    return;
                _connection = null;
                Drop(null);
                await connection.DisposeAsync().ConfigureAwait(false);
                if (deps.Time.GetUtcNow() - _upSince >= Options.StableUptime)
                    _step = 0;
                var wait = Options.Backoff[Math.Min(_step++, Options.Backoff.Count - 1)];
                _restarts++;
                LogHostExited(_userKey, code, wait.TotalSeconds);
                _restartTimer = deps.Time.CreateTimer(_ => _ = RestartAsync(), null, wait, Timeout.InfiniteTimeSpan);
                Set(ModHostStates.Restarting, $"The mod host stopped (exit code {code}); restarting in {wait.TotalSeconds:0.#} s.");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            LogFailed(ex, _userKey);
        }
    }

    private async Task RestartAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _restartTimer?.Dispose();
                _restartTimer = null;
                if (!_shutdown)
                    await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            LogFailed(ex, _userKey);
        }
    }

    /// <summary>Stops the host on purpose: what it loaded and the sessions it saw go with it.</summary>
    private async Task StopHostAsync()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
        lock (_sync)
            _seen.Clear();
        if (Interlocked.Exchange(ref _connection, null) is not { } connection)
            return;
        Drop(null);
        try
        {
            await connection.ShutdownAsync(Options.ShutdownGrace).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailed(ex, _userKey);
        }
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void Set(string state, string? reason, IModHostConnection? connection = null, string? bunPath = null)
    {
        var status = new ModHostStatus(state, reason, connection?.ProcessId, bunPath, connection?.Host.HostVersion, _restarts);
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous.State != state || previous.Reason != reason)
            LogState(_userKey, state, status.ProcessId, reason);
        Changed?.Invoke(status);
    }

    /// <summary>Deletes a staged copy with its generation folder, which holds nothing else.</summary>
    private void DeleteStaged(string? staged) => DeleteFolder(staged is null ? null : Path.GetDirectoryName(staged));

    private void DeleteFolder(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailed(ex, _userKey);
        }
    }

    private static bool Gone(Exception ex) => ex is ModHostNotReadyException or ModHostClosedException;

    /// <summary>Waits for a request whose error answer doesn't matter; a host that's gone still throws.</summary>
    private static async Task Quietly(Task request)
    {
        try
        {
            await request.ConfigureAwait(false);
        }
        catch (ModHostRpcException)
        {
        }
    }

    // ── What the host asks of Fleet ─────────────────────────────────────

    async Task<JsonElement> IModHostCalls.HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (!Calls.Contains(method))
            throw new ModHostRpcException(ModHostErrorCodes.MethodNotFound, $"Fleet doesn't answer {method}.");
        var modId = Text(parameters, "mod");
        var sessionId = Required(parameters, "sessionId");
        var route = Volatile.Read(ref _routes).FirstOrDefault(r => r.Id == modId) ?? throw Invalid($"{modId} isn't loaded.");
        if (route.DraftSessionId is { } own && own != sessionId)
            throw Invalid($"{modId} runs only in its own session.");

        // A draft shares the store of the kept mod with its name.
        switch (method)
        {
            case "store.get":
                var value = await deps.Store.GetValueAsync(userId, route.Name, StoreKey(parameters), ct).ConfigureAwait(false);
                return value is { } found ? Params(w => { w.WritePropertyName("value"); found.WriteTo(w); }) : Empty;
            case "store.set" when parameters.TryGetProperty("value", out var set):
                try
                {
                    await deps.Store.SetValueAsync(userId, route.Name, StoreKey(parameters), set, ct).ConfigureAwait(false);
                    return Empty;
                }
                catch (Exception ex) when (ex is ModStoreFullException or ArgumentException)
                {
                    throw Invalid(ex.Message);
                }
            case "store.delete":
                await deps.Store.DeleteValueAsync(userId, route.Name, StoreKey(parameters), ct).ConfigureAwait(false);
                return Empty;
            case "store.keys":
                var keys = await deps.Store.KeysAsync(userId, route.Name, ct).ConfigureAwait(false);
                return Params(w => Strings(w, "keys", keys));
            case "session.get":
                var session = await deps.FindSession(userId, sessionId, ct).ConfigureAwait(false);
                if (session is null || session.UserId != userId)
                    throw Invalid($"There is no session {sessionId}.");
                return Params(w =>
                {
                    w.WriteString("id", session.Id);
                    w.WriteString("title", session.Title);
                    w.WriteString("harness", session.HarnessType);
                    w.WriteString("cwd", session.Directory);
                    Strings(w, "surfaces", deps.Ui.SurfacesOf(userId, sessionId));
                });
            case "ui.open":
                await deps.Ui.OpenPaneAsync(userId, route.Id, sessionId, Required(parameters, "id"), Text(parameters, "title"), ct).ConfigureAwait(false);
                return Empty;
            case "ui.close":
                await deps.Ui.ClosePaneAsync(userId, route.Id, sessionId, Required(parameters, "id"), ct).ConfigureAwait(false);
                return Empty;
            case "ui.toast":
                int? timeoutMs = parameters.TryGetProperty("timeoutMs", out var timeout) && timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out var ms) ? ms : null;
                await deps.Ui.ToastAsync(userId, route.Id, sessionId, Required(parameters, "text"), timeoutMs, Text(parameters, "tone"), ct).ConfigureAwait(false);
                return Empty;
            default:
                throw Invalid($"{method} needs a value.");
        }
    }

    void IModHostCalls.HandleNotification(string method, JsonElement parameters)
    {
        try
        {
            if (Text(parameters, "mod") is not { } modId)
                return;
            if (method == "invalidate")
                deps.Ui.Invalidated(userId, modId, Text(parameters, "sessionId"));
            else if (method == "log")
                deps.Ui.Logged(userId, modId, Text(parameters, "sessionId"), Text(parameters, "level") ?? "info", Text(parameters, "text") ?? "");
            else if (method == "failed")
                Strike(parameters);
        }
        catch (Exception ex)
        {
            LogFailed(ex, _userKey);
        }
    }

    private static string? Text(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Required(JsonElement parameters, string name) => Text(parameters, name) ?? throw Invalid($"\"{name}\" is required.");

    private static string StoreKey(JsonElement parameters)
        => Text(parameters, "key") is { } key && ModNames.IsValidStoreKey(key) ? key : throw Invalid("A store key is 1 to 64 letters, digits, _, - and .");

    private static JsonElement[] Items(JsonElement answer, string name)
        => answer.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? [.. array.EnumerateArray()] : [];

    private static List<ModHookSpec> HooksOf(JsonElement answer)
        => answer.ValueKind != JsonValueKind.Object
            ? []
            : [.. Items(answer, "hooks").Where(h => Text(h, "event") is not null)
                .Select(h => new ModHookSpec(Text(h, "event")!, h.TryGetProperty("matcher", out var matcher) ? matcher.Clone() : null))];

    private static ModHostRpcException Invalid(string message) => new(ModHostErrorCodes.InvalidParams, message);

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    /// <summary>A JSON object written directly: no reflection, so it trims.</summary>
    private static JsonElement Params(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <param name="Number">A kept mod's version; null for a draft.</param>
    /// <param name="SessionId">A draft's session; null for a kept mod.</param>
    private sealed record WantedMod(string Id, string Name, int? Number, string? SessionId);

    /// <param name="Staged">A draft's staged copy, deleted when it's replaced or unloaded.</param>
    private sealed record LoadedMod(ModRoute Route, string? Staged);

    [LoggerMessage(Level = LogLevel.Information, Message = "The mod host for {UserKey} is {State} (pid {ProcessId}): {Reason}")]
    private partial void LogState(string userKey, string state, int? processId, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mod host for {UserKey} exited with code {Code}; restarting in {Seconds} s")]
    private partial void LogHostExited(string userKey, int code, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mod host for {UserKey} didn't answer {Method} in time; killing it")]
    private partial void LogRequestTimedOut(string userKey, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mod host for {UserKey} failed")]
    private partial void LogFailed(Exception ex, string userKey);
}
