using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>Finds one of the user's sessions, as that user; null when they have no such session.</summary>
internal delegate Task<Session?> ModSessionLookup(string userId, string sessionId, CancellationToken ct);

/// <summary>What every user's supervisor shares.</summary>
internal sealed record ModHostDependencies(
    IModHostConnectionFactory Connections,
    IModUserGate Gate,
    IModStrikeRecorder StrikeRecorder,
    IModHostUi Ui,
    IModHostSignals Signals,
    IModHostBun Bun,
    IModHostFiles Files,
    IModDraftWatcher Watcher,
    IModVersionStore Store,
    ModSessionLookup FindSession,
    ModLogBook Log,
    TimeProvider Time,
    ILogger Logger);

/// <summary>
/// One user's mod host (<c>docs/mods/api.md</c>, "The protocol"): starts it when a mod should run or a check needs it,
/// keeps what it has loaded in step with the store, runs events through it, answers its <c>$</c> calls, counts strikes,
/// and restarts it with backoff when it dies or stops answering.
/// </summary>
/// <remarks>
/// Lifecycle steps (reconcile, start, stop, a crash, a struck mod leaving) run one at a time under a gate. Dispatches and
/// <c>$</c> answers never take it: they read the routing snapshot, swapped whole after each load and unload. Nothing that
/// raises <c>mods.changed</c> runs under the gate, because <c>mods.changed</c> reconciles, which needs it.
/// </remarks>
internal sealed partial class ModHostSupervisor : IModHostCalls, IAsyncDisposable
{
    /// <summary>How long Fleet waits for a dispatch before it restarts the host (<c>Limits</c>).</summary>
    public static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a dispatch waits for a host that is starting before it gives up.</summary>
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a host has to exit after <c>shutdown</c> before it's killed.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long a check keeps the host running after it, for the checks that usually follow.</summary>
    public static readonly TimeSpan CheckLease = TimeSpan.FromSeconds(60);

    /// <summary>A host up this long has its backoff start over.</summary>
    public static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);

    /// <summary>The waits before restarting a host that died, one after another; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    ];

    private const string HangExitReason = "didn't answer a dispatch within 15 s";

    private static readonly HashSet<string> ControlEvents = new(StringComparer.Ordinal) { "ui.press", "ui.input", "ui.select" };

    private static readonly HashSet<string> Calls = new(StringComparer.Ordinal)
    {
        "store.get", "store.set", "store.delete", "store.keys", "session.get", "ui.open", "ui.close", "ui.toast",
    };

    private static readonly JsonElement EmptyAnswer = Answer(_ => { });

    private readonly ModHostDependencies _deps;
    private readonly IModVersionStore _store;
    private readonly ILogger _logger;
    private readonly string _userKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // ── Under the gate ──
    private readonly Dictionary<string, LoadedMod> _loaded = new(StringComparer.Ordinal);
    private readonly List<Action> _afterGate = [];
    private IDisposable? _watch;
    private bool _inBackoff;
    private ITimer? _backoffTimer;
    private int _backoffStep;
    private bool _bunMissingLogged;

    // ── Read anywhere ──
    private volatile HostRun? _run;
    private volatile IReadOnlyList<ModRoute> _routes = [];
    private volatile bool _stopped;
    private long _generation;
    private readonly ModStrikes _strikes = new();
    private readonly ConcurrentDictionary<string, ModLoadProblem> _problems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _refused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reloadOwed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _reloadStarts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _turningOff = new(StringComparer.Ordinal);

    // ── Status, under _sync ──
    private readonly Lock _sync = new();
    private string _state = ModHostStates.Stopped;
    private string? _reason;
    private int _restarts;
    private ModHostExit? _lastExit;
    private TaskCompletionSource _ready = Completed();
    private DateTimeOffset? _leaseUntil;
    private ITimer? _leaseTimer;

    public ModHostSupervisor(string userId, ModHostDependencies deps)
    {
        UserId = userId;
        _deps = deps;
        _store = deps.Store;
        _logger = deps.Logger;
        _userKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16];
    }

    public string UserId { get; }

    private DateTimeOffset Now => _deps.Time.GetUtcNow();

    /// <summary>Where staged copies of the user's drafts go: <c>{host}/drafts/{sessionId}/{generation}/{name}/</c>.</summary>
    private string StagedRoot => Path.Combine(_store.HostFolder(UserId), "drafts");

    // ── Status ──────────────────────────────────────────────────────────

    public ModHostStatus GetStatus()
    {
        var run = _run;
        var loaded = _routes.Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
        lock (_sync)
        {
            return new ModHostStatus(
                _state,
                _reason,
                run?.Connection.ProcessId,
                run?.Bun.ExecutablePath,
                run is null ? null : run.Bun.Version ?? run.Connection.Host.BunVersion,
                run?.Connection.Host.HostVersion,
                loaded,
                _restarts,
                run?.StartedAt,
                _lastExit);
        }
    }

    public ModLoadProblem? GetLoadProblem(string modId) => _problems.GetValueOrDefault(modId);

    internal int StrikesOf(string modId) => _strikes.Of(modId);

    /// <summary>The store changed: kept mods the host refused may load now.</summary>
    public void ForgetRefusals() => _refused.Clear();

    private void Enter(string state, string? reason)
    {
        TaskCompletionSource? settled = null;
        lock (_sync)
        {
            _state = state;
            _reason = reason;
            if (state is ModHostStates.Starting or ModHostStates.Restarting)
            {
                if (_ready.Task.IsCompleted)
                    _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                settled = _ready;
            }
        }
        settled?.TrySetResult();
    }

    private void NotReady(string reason)
    {
        LogNotReady(_logger, _userKey, reason);
        Enter(ModHostStates.NotReady, reason);
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    // ── Reconcile ───────────────────────────────────────────────────────

    /// <summary>Brings the host in line with the store, the Mods switch, the check lease and Bun.</summary>
    public Task EnsureAsync(CancellationToken ct = default) => GatedAsync(ReconcileAsync, ct);

    /// <summary>Runs one lifecycle step under the gate, then whatever it left to do outside it.</summary>
    private async Task GatedAsync(Func<CancellationToken, Task> step, CancellationToken wait)
    {
        if (_stopped)
            return;
        try
        {
            await _gate.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Action[] after;
        try
        {
            if (!_stopped)
                await step(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            LogStepFailed(_logger, _userKey, e);
        }
        finally
        {
            after = [.. _afterGate];
            _afterGate.Clear();
            _gate.Release();
        }

        foreach (var action in after)
            action();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var switchedOn = await _deps.Gate.IsSwitchedOnAsync(UserId, ct).ConfigureAwait(false);
        var safeMode = _deps.Gate.IsSafeMode(UserId);
        var desired = switchedOn && !safeMode ? await DesiredAsync(ct).ConfigureAwait(false) : [];
        // A check runs no mod code, so safe mode doesn't stop one.
        var leased = switchedOn && LeaseActive();

        if (desired.Count == 0 && !leased)
        {
            await StopAsync(!switchedOn ? "Mods are off." : safeMode ? "Started without mods." : "No mod is kept or drafted.").ConfigureAwait(false);
            return;
        }

        if (_run is not { } run)
        {
            // The backoff's timer reconciles when it ends.
            if (!_inBackoff)
                await StartAsync(desired, bun: null, ct).ConfigureAwait(false);
            return;
        }

        if (await NewBunAsync(run, ct).ConfigureAwait(false) is { } bun)
        {
            await MoveToBunAsync(run, bun, desired, ct).ConfigureAwait(false);
            return;
        }

        await SyncAsync(run, desired, ct).ConfigureAwait(false);
    }

    /// <summary>The mods that should be loaded now, kept ones first, then drafts.</summary>
    private async Task<List<WantedMod>> DesiredAsync(CancellationToken ct)
    {
        var wanted = new List<WantedMod>();
        foreach (var history in await _store.ListAsync(UserId, ct).ConfigureAwait(false))
        {
            if (history.Off is null && history.Active is { } number && history.Versions.Any(v => v.Number == number))
                wanted.Add(new WantedMod(ModIds.Kept(history.Name, number), history.Name, number, null));
        }

        foreach (var sessionId in await _store.ListDraftSessionsAsync(UserId, ct).ConfigureAwait(false))
        {
            var session = await _deps.FindSession(UserId, sessionId, ct).ConfigureAwait(false);
            if (session is null || string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
                continue;
            foreach (var draft in await _store.ListDraftsAsync(UserId, sessionId, ct).ConfigureAwait(false))
            {
                if (draft.Off is null && draft.Manifest is not null)
                    wanted.Add(new WantedMod(ModIds.Draft(draft.Name, sessionId), draft.Name, null, sessionId));
            }
        }

        return wanted;
    }

    /// <summary>Unloads what's no longer wanted, then loads what's missing and reloads drafts saved since they loaded.</summary>
    private async Task SyncAsync(HostRun run, List<WantedMod> desired, CancellationToken ct)
    {
        var wantedIds = desired.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var id in _loaded.Keys.Where(id => !wantedIds.Contains(id)).ToList())
                await UnloadAsync(run, id, ct).ConfigureAwait(false);

            foreach (var mod in desired)
            {
                if (_loaded.ContainsKey(mod.Id))
                {
                    if (mod.SessionId is not null && _dirty.ContainsKey(mod.Id))
                        await LoadAsync(run, mod, restoring: false, ct).ConfigureAwait(false);
                }
                else if (!_refused.ContainsKey(mod.Id))
                {
                    await LoadAsync(run, mod, restoring: false, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A closed pipe or a host that stopped answering: the crash path restarts it.
            LogHostFailed(_logger, _userKey, e);
            KillQuietly(run);
        }

        foreach (var id in _problems.Keys.Concat(_refused.Keys).Concat(_dirty.Keys).Where(id => !wantedIds.Contains(id)).ToList())
        {
            _problems.TryRemove(id, out _);
            _refused.TryRemove(id, out _);
            _dirty.TryRemove(id, out _);
        }
    }

    // ── Start, stop, Bun ────────────────────────────────────────────────

    private async Task StartAsync(List<WantedMod> desired, BunLocation? bun, CancellationToken ct)
    {
        if (_deps.Files.HostScript is not { } script)
        {
            NotReady("Fleet can't find the mod host (mods-host/host.js).");
            return;
        }
        bun ??= await _deps.Bun.FindAsync(ct).ConfigureAwait(false);
        if (bun is null)
        {
            NotReady("The mod runtime (Bun) isn't installed yet.");
            return;
        }

        Enter(ModHostStates.Starting, null);
        var hostFolder = _store.HostFolder(UserId);
        IModHostConnection connection;
        try
        {
            Directory.CreateDirectory(hostFolder);
            var launch = new ModHostLaunch(bun.ExecutablePath, script, hostFolder, _deps.Files.FleetVersion, _userKey);
            connection = await _deps.Connections.StartAsync(launch, this, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogStartFailed(_logger, _userKey, e);
            NotReady(e is ModHostNotReadyException ? e.Message : $"The mod host didn't start: {e.Message}");
            return;
        }

        var run = new HostRun(connection, bun, Now);
        if (_stopped)
        {
            await StopRunAsync(run).ConfigureAwait(false);
            return;
        }

        _run = run;
        _ = WatchExitAsync(run);
        _seen.Clear();
        DeleteFolder(StagedRoot);
        _watch ??= _deps.Watcher.Watch(_store.DraftsRoot(UserId), OnDraftChanged);
        LogStarted(_logger, _userKey, connection.ProcessId, bun.ExecutablePath);

        try
        {
            foreach (var mod in desired.Where(m => !_refused.ContainsKey(m.Id)))
                await LoadAsync(run, mod, restoring: true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogHostFailed(_logger, _userKey, e);
            KillQuietly(run);
            return;
        }

        // Sessions the last host had started get session.start again, before anything else of theirs is sent.
        var owed = new List<(string SessionId, TaskCompletionSource Sent)>();
        foreach (var sessionId in _reloadOwed.Keys.Order(StringComparer.Ordinal))
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _reloadStarts[sessionId] = sent.Task;
            owed.Add((sessionId, sent));
        }
        _reloadOwed.Clear();

        Enter(ModHostStates.Running, null);
        if (owed.Count > 0)
            _ = SendReloadStartsAsync(run, owed);
    }

    /// <summary>Nothing needs the host: stops it, and forgets what it held.</summary>
    private async Task StopAsync(string reason)
    {
        CancelBackoff();
        if (_run is { } run)
        {
            await StopRunAsync(run).ConfigureAwait(false);
            LogStopped(_logger, _userKey, reason);
        }
        _seen.Clear();
        _reloadOwed.Clear();
        _problems.Clear();
        _dirty.Clear();
        _watch?.Dispose();
        _watch = null;
        DeleteFolder(StagedRoot);
        Enter(ModHostStates.Stopped, reason);
    }

    /// <summary>Stops a host Fleet no longer wants: <c>shutdown</c>, then killed after the grace.</summary>
    private async Task StopRunAsync(HostRun run)
    {
        run.Planned = true;
        if (ReferenceEquals(_run, run))
            _run = null;
        _loaded.Clear();
        Publish();
        try
        {
            await run.Connection.ShutdownAsync(ShutdownGrace).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            LogShutdownFailed(_logger, _userKey, e);
        }
        try
        {
            await run.Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            LogShutdownFailed(_logger, _userKey, e);
        }
    }

    /// <summary>The Bun to move to, when it isn't the one the host runs on; null to stay.</summary>
    private async Task<BunLocation?> NewBunAsync(HostRun run, CancellationToken ct)
    {
        var bun = await _deps.Bun.FindAsync(ct).ConfigureAwait(false);
        if (bun is null)
        {
            // Bun is being replaced: a working host keeps running.
            if (!_bunMissingLogged)
                LogBunMissing(_logger, _userKey);
            _bunMissingLogged = true;
            return null;
        }
        _bunMissingLogged = false;
        return string.Equals(bun.ExecutablePath, run.Bun.ExecutablePath, StringComparison.Ordinal)
               && string.Equals(bun.Version, run.Bun.Version, StringComparison.Ordinal)
            ? null
            : bun;
    }

    /// <summary>A planned restart on a new Bun: not a crash, no backoff. The old Bun is pruned once the new host runs.</summary>
    private async Task MoveToBunAsync(HostRun run, BunLocation bun, List<WantedMod> desired, CancellationToken ct)
    {
        LogMovingToBun(_logger, _userKey, bun.ExecutablePath);
        OweReloadStarts();
        await StopRunAsync(run).ConfigureAwait(false);
        await StartAsync(desired, bun, ct).ConfigureAwait(false);
        _afterGate.Add(() => _deps.Signals.Restarted(UserId));
        if (_run is null || GetStatus().State != ModHostStates.Running)
            return;
        try
        {
            await _deps.Bun.PruneAsync([bun.ExecutablePath], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogPruneFailed(_logger, e);
        }
    }

    private void OweReloadStarts()
    {
        foreach (var sessionId in _seen.Keys)
            _reloadOwed.TryAdd(sessionId, 0);
        _seen.Clear();
    }

    // ── Loads ───────────────────────────────────────────────────────────

    /// <summary>
    /// Loads (or, for a loaded draft, reloads) one mod. A refusal is kept as a problem; anything else (a closed pipe, a
    /// timeout) is thrown for the caller to hand to the crash path.
    /// </summary>
    private async Task LoadAsync(HostRun run, WantedMod mod, bool restoring, CancellationToken ct)
    {
        _dirty.TryRemove(mod.Id, out _);
        if (mod.SessionId is not { } sessionId)
        {
            var number = mod.Number!.Value;
            ModLoadResult kept;
            try
            {
                kept = await run.Connection.LoadAsync(ModLoadParams.Kept(mod.Name, number, _store.VersionFolder(UserId, mod.Name, number)), ct).ConfigureAwait(false);
            }
            catch (ModHostRpcException e)
            {
                Refused(mod, e.Message, e.Data);
                return;
            }
            Loaded(mod, kept, stagedFolder: null, restoring);
            return;
        }

        // The host reads a draft from a copy, never from the folder the agent writes; the copy's leaf is named after the
        // mod, as the host's check requires.
        var generation = Path.Combine(StagedRoot, sessionId, Interlocked.Increment(ref _generation).ToString(CultureInfo.InvariantCulture));
        var staged = Path.Combine(generation, mod.Name);
        try
        {
            await _store.StageDraftAsync(UserId, sessionId, mod.Name, staged, ct).ConfigureAwait(false);
        }
        catch (ModStoreException e)
        {
            DeleteFolder(generation);
            Refused(mod, e.Message, report: null);
            return;
        }

        ModLoadResult draft;
        try
        {
            draft = await run.Connection.LoadAsync(ModLoadParams.Draft(mod.Name, sessionId, staged), ct).ConfigureAwait(false);
        }
        catch (ModHostRpcException e)
        {
            // The host keeps the old module when a reload fails, so its copy stays too.
            DeleteFolder(generation);
            Refused(mod, e.Message, e.Data);
            return;
        }
        catch
        {
            DeleteFolder(generation);
            throw;
        }

        // Only now does the host read the new copy: Page paths are read from the loaded root when drawn.
        var previous = _loaded.GetValueOrDefault(mod.Id);
        Loaded(mod, draft, generation, restoring);
        if (previous?.StagedFolder is { } old)
            DeleteFolder(old);
    }

    private void Loaded(WantedMod mod, ModLoadResult result, string? stagedFolder, bool restoring)
    {
        _loaded[mod.Id] = new LoadedMod(new ModRoute(mod.Id, mod.Name, mod.SessionId, result.Hooks), stagedFolder);
        Publish();
        _problems.TryRemove(mod.Id, out _);
        _refused.TryRemove(mod.Id, out _);
        // Loading again after a restart keeps Fleet's count: that's what it's for. A new load or a saved draft starts over.
        if (!restoring)
            _strikes.Reset(mod.Id);
    }

    private void Refused(WantedMod mod, string message, JsonElement? report)
    {
        var now = Now;
        _problems[mod.Id] = new ModLoadProblem(message, report, now);
        _refused[mod.Id] = 0;
        _deps.Log.Add(UserId, mod.Id, new ModLogLine(now, "error", message, mod.SessionId));
        LogLoadRefused(_logger, mod.Id, _userKey, message);
    }

    private async Task UnloadAsync(HostRun run, string id, CancellationToken ct)
    {
        var entry = _loaded[id];
        try
        {
            await run.Connection.UnloadAsync(id, ct).ConfigureAwait(false);
        }
        catch (ModHostRpcException e)
        {
            LogUnloadRefused(_logger, id, e.Message);
        }
        _loaded.Remove(id);
        Publish();
        if (entry.StagedFolder is { } staged)
            DeleteFolder(staged);
        _strikes.Reset(id);
    }

    /// <summary>Swaps the routing snapshot dispatches read.</summary>
    private void Publish() => _routes = [.. _loaded.Values.Select(l => l.Route)];

    // ── Drafts ──────────────────────────────────────────────────────────

    private void OnDraftChanged(ModDraftChange change)
    {
        if (_stopped)
            return;
        var id = ModIds.Draft(change.Name, change.SessionId);
        _dirty[id] = 0;
        _refused.TryRemove(id, out _);
        _ = Task.Run(() => EnsureAsync());
    }

    /// <summary>The session is archived or gone: the host drops what it holds for it, and Fleet its staged copies.</summary>
    public async Task ForgetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _seen.TryRemove(sessionId, out _);
        _reloadOwed.TryRemove(sessionId, out _);
        if (_run is { } run)
        {
            try
            {
                await run.Connection.ForgetAsync(sessionId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogForgetFailed(_logger, _userKey, e);
            }
        }

        if (!ModNames.IsValidSessionId(sessionId))
            return;
        await GatedAsync(_ =>
        {
            // A draft of the session that is still loaded keeps its copy; unloading it deletes it.
            var keep = _loaded.Values
                .Where(l => l.StagedFolder is not null && string.Equals(l.Route.DraftSessionId, sessionId, StringComparison.Ordinal))
                .Select(l => l.StagedFolder!)
                .ToHashSet(StringComparer.Ordinal);
            var folder = Path.Combine(StagedRoot, sessionId);
            if (keep.Count == 0)
                DeleteFolder(folder);
            else if (Directory.Exists(folder))
            {
                foreach (var generation in Directory.GetDirectories(folder).Where(g => !keep.Contains(g)))
                    DeleteFolder(generation);
            }
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(_logger, _userKey, e);
        }
    }

    // ── Checks ──────────────────────────────────────────────────────────

    /// <summary>Runs the static check, starting the host for <see cref="CheckLease"/> if nothing else needs it.</summary>
    public async Task<JsonElement> CheckAsync(string folder, CancellationToken ct = default)
    {
        if (_stopped)
            throw new ModHostNotReadyException("Fleet is shutting down.");
        if (!await _deps.Gate.IsSwitchedOnAsync(UserId, ct).ConfigureAwait(false))
            throw new ModHostNotReadyException(ModsFeature.TurnedOffMessage);

        ExtendLease();
        await EnsureAsync(ct).ConfigureAwait(false);
        var run = _run;
        var status = GetStatus();
        if (run is null || status.State != ModHostStates.Running)
            throw new ModHostNotReadyException(status.Reason ?? "The mod host isn't running.");

        try
        {
            return await run.Connection.CheckAsync(folder, Path.Combine(folder, "mod.json"), ct).ConfigureAwait(false);
        }
        catch (ModHostClosedException)
        {
            throw new ModHostNotReadyException("The mod host stopped during the check.");
        }
        catch (ModHostRpcException e)
        {
            throw new ModHostNotReadyException(e.Message);
        }
    }

    private void ExtendLease()
    {
        lock (_sync)
        {
            _leaseUntil = Now + CheckLease;
            if (_leaseTimer is null)
                _leaseTimer = _deps.Time.CreateTimer(_ => _ = Task.Run(() => EnsureAsync()), null, CheckLease, Timeout.InfiniteTimeSpan);
            else
                _leaseTimer.Change(CheckLease, Timeout.InfiniteTimeSpan);
        }
    }

    private bool LeaseActive()
    {
        lock (_sync)
            return _leaseUntil is { } until && Now < until;
    }

    // ── Dispatch ────────────────────────────────────────────────────────

    public async Task<ModDispatchResult> DispatchAsync(ModDispatchRequest request, CancellationToken ct = default)
    {
        var run = await RunningAsync(ct).ConfigureAwait(false);
        if (run is null)
            return ModDispatchResult.NotDispatched;

        // After a restart, the session's mods get session.start (reload) before anything else, so the host never starts
        // them implicitly with "start".
        if (_reloadStarts.TryGetValue(request.SessionId, out var reloadStart))
        {
            await reloadStart.WaitAsync(ct).ConfigureAwait(false);
            if (!ReferenceEquals(_run, run))
                return ModDispatchResult.NotDispatched;
        }

        // A host with no mod (one up only for checks) runs nothing, not even a control's callback.
        var routes = _routes;
        if (routes.Count == 0)
            return ModDispatchResult.NotDispatched;

        var chain = ModRouting.ChainFor(routes, request.Event, request.SessionId, request.E);
        if (chain.Count == 0 && !ControlEvents.Contains(request.Event))
            return ModDispatchResult.NotDispatched;

        return await SendAsync(run, request.Event, request.SessionId, request.E, chain, request.Surface, ct).ConfigureAwait(false);
    }

    /// <summary>The running host; waits up to <see cref="ReadyWait"/> for one that is starting. Null when there's none.</summary>
    private async Task<HostRun?> RunningAsync(CancellationToken ct)
    {
        string state;
        Task ready;
        lock (_sync)
        {
            state = _state;
            ready = _ready.Task;
        }
        if (state is ModHostStates.Starting or ModHostStates.Restarting)
        {
            try
            {
                await ready.WaitAsync(ReadyWait, _deps.Time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            lock (_sync)
                state = _state;
        }
        return state == ModHostStates.Running ? _run : null;
    }

    private async Task<ModDispatchResult> SendAsync(HostRun run, string @event, string sessionId, JsonElement e, IReadOnlyList<string> chain, string? surface, CancellationToken ct)
    {
        _seen.TryAdd(sessionId, 0);
        // Only what the host says is running from here on can be this dispatch's.
        var sentAfter = run.RunningCount;
        ModWireDispatchResult answer;
        try
        {
            answer = await run.Connection.DispatchAsync(new ModWireDispatch(@event, sessionId, e, chain, surface), DispatchTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Hung(run, @event, sessionId, chain, sentAfter);
            return ModDispatchResult.NotDispatched;
        }
        catch (ModHostRpcException error) when (error.Code == ModHostErrorCodes.InvalidParams)
        {
            // An expired handle, say: the client draws the site again.
            return ModDispatchResult.NotDispatched;
        }
        catch (Exception error)
        {
            // A closed pipe or a host being replaced: the crash path restarts it.
            LogDispatchFailed(_logger, @event, _userKey, error);
            return ModDispatchResult.NotDispatched;
        }

        var failures = answer.Failures ?? [];
        Count(failures, chain, sessionId);
        return new ModDispatchResult(true, answer.Result, answer.DrawnBy ?? [], failures);
    }

    private async Task SendReloadStartsAsync(HostRun run, List<(string SessionId, TaskCompletionSource Sent)> owed)
    {
        foreach (var (sessionId, sent) in owed)
        {
            try
            {
                if (ReferenceEquals(_run, run))
                {
                    var e = Answer(w =>
                    {
                        w.WriteString("sessionId", sessionId);
                        w.WriteString("reason", "reload");
                    });
                    var chain = ModRouting.ChainFor(_routes, "session.start", sessionId, e);
                    if (chain.Count > 0)
                        await SendAsync(run, "session.start", sessionId, e, chain, null, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                LogDispatchFailed(_logger, "session.start", _userKey, error);
            }
            finally
            {
                _reloadStarts.TryRemove(new KeyValuePair<string, Task>(sessionId, sent.Task));
                sent.TrySetResult();
            }
        }
    }

    // ── Strikes ─────────────────────────────────────────────────────────

    /// <summary>Counts a dispatch's failures; every mod in the chain that didn't fail starts its count over.</summary>
    private void Count(IReadOnlyList<ModHookFailure> failures, IReadOnlyList<string> chain, string sessionId)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            failed.Add(failure.Mod);
            Failed(failure.Mod, failure.Event, failure.Kind, failure.Message, failure.Strikes, failure.SessionId ?? sessionId);
        }
        foreach (var mod in chain.Where(m => !failed.Contains(m)))
            _strikes.Reset(mod);
    }

    private void Failed(string modId, string @event, string kind, string message, int hostStrikes, string? sessionId)
    {
        var count = _strikes.Failed(modId, hostStrikes);
        _deps.Log.Add(UserId, modId, new ModLogLine(Now, "error", $"{@event} {kind}: {message}", sessionId));
        if (count >= ModStrikes.Limit)
            TurnOff(modId, message);
    }

    /// <summary>
    /// The host didn't answer: blames the mod it last said was running for this dispatch, else a lone mod in the chain,
    /// else the outermost; kills the host, whose exit restarts it. Only the first timeout on a host does this.
    /// </summary>
    /// <remarks>
    /// "For this dispatch" is the session and event, announced after the dispatch was sent; or the session's
    /// <c>session.start</c>, which the host runs first, inside the dispatch, for a mod that hasn't started there yet.
    /// </remarks>
    private void Hung(HostRun run, string @event, string sessionId, IReadOnlyList<string> chain, long sentAfter)
    {
        if (!run.ClaimHang())
            return;
        run.Hung = true;

        string? named = null;
        var latest = sentAfter;
        foreach (var key in new[] { (sessionId, @event), (sessionId, "session.start") })
        {
            if (run.Running.TryGetValue(key, out var said) && said.Sequence > latest)
                (named, latest) = (said.Mod, said.Sequence);
        }
        var struck = named ?? (chain.Count > 0 ? chain[0] : null);
        var suspects = chain.ToList();
        if (named is not null && !suspects.Contains(named, StringComparer.Ordinal))
            suspects.Add(named);

        var text = $"The mod host didn't answer within 15 s while running {@event}; Fleet restarted it. Suspects: {string.Join(", ", suspects)}; struck: {struck}.";
        var now = Now;
        foreach (var suspect in suspects)
            _deps.Log.Add(UserId, suspect, new ModLogLine(now, "error", text, sessionId));
        LogHung(_logger, _userKey, @event, struck ?? "nobody");

        KillQuietly(run);
        if (struck is not null && _strikes.Hung(struck) >= ModStrikes.Limit)
            TurnOff(struck, text);
    }

    /// <summary>
    /// Three strikes: records it in the store (which raises <c>mods.changed</c>) off the gate, once while it's in flight,
    /// then takes the mod out of the host.
    /// </summary>
    private void TurnOff(string modId, string message)
    {
        if (!_turningOff.TryAdd(modId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var name = ModIds.NameOf(modId);
                if (ModIds.DraftSessionOf(modId) is { } sessionId)
                    await _deps.StrikeRecorder.RecordDraftAsync(UserId, sessionId, name, message, _lifetime.Token).ConfigureAwait(false);
                else
                    await _deps.StrikeRecorder.RecordKeptAsync(UserId, name, message, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LogStrikeRecordFailed(_logger, modId, _userKey, e);
            }

            try
            {
                await GatedAsync(ct => DropStruckAsync(modId, ct), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _strikes.Reset(modId);
                _turningOff.TryRemove(modId, out _);
            }
        });
    }

    private async Task DropStruckAsync(string modId, CancellationToken ct)
    {
        // Not loaded again until the store changes, even if recording the strikes failed.
        _refused[modId] = 0;
        if (!_loaded.TryGetValue(modId, out var entry))
            return;
        if (_run is { } run)
        {
            try
            {
                await run.Connection.UnloadAsync(modId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The host unloads a mod at its own third strike.
                LogUnloadRefused(_logger, modId, e.Message);
            }
        }
        _loaded.Remove(modId);
        Publish();
        if (entry.StagedFolder is { } staged)
            DeleteFolder(staged);
    }

    // ── Crashes ─────────────────────────────────────────────────────────

    private async Task WatchExitAsync(HostRun run)
    {
        int? code;
        try
        {
            code = await run.Connection.Exited.ConfigureAwait(false);
        }
        catch (Exception)
        {
            code = null;
        }
        if (run.Planned || _stopped)
            return;
        await GatedAsync(_ => CrashedAsync(run, code), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The host exited without Fleet asking: counted, and started again after the backoff.</summary>
    private async Task CrashedAsync(HostRun run, int? code)
    {
        if (!ReferenceEquals(_run, run) || run.Planned)
            return;

        _run = null;
        OweReloadStarts();
        _loaded.Clear();
        Publish();
        DeleteFolder(StagedRoot);

        var now = Now;
        var exit = run.Hung
            ? new ModHostExit(now, null, HangExitReason)
            : new ModHostExit(now, code, $"exited with code {code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        if (now - run.StartedAt >= StableUptime)
            _backoffStep = 0;
        var delay = Backoff[Math.Min(_backoffStep, Backoff.Count - 1)];
        _backoffStep++;

        lock (_sync)
        {
            _restarts++;
            _lastExit = exit;
        }
        Enter(ModHostStates.Restarting, $"The mod host {exit.Reason}; Fleet is starting it again.");
        LogExited(_logger, _userKey, exit.Reason, delay);

        try
        {
            await run.Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            LogShutdownFailed(_logger, _userKey, e);
        }

        _inBackoff = true;
        _backoffTimer?.Dispose();
        _backoffTimer = _deps.Time.CreateTimer(_ => _ = Task.Run(BackoffEndedAsync), null, delay, Timeout.InfiniteTimeSpan);
        _afterGate.Add(() => _deps.Signals.Restarted(UserId));
    }

    private Task BackoffEndedAsync() => GatedAsync(ct =>
    {
        CancelBackoff();
        return ReconcileAsync(ct);
    }, CancellationToken.None);

    private void CancelBackoff()
    {
        _inBackoff = false;
        _backoffTimer?.Dispose();
        _backoffTimer = null;
    }

    private void KillQuietly(HostRun run)
    {
        try
        {
            run.Connection.Kill();
        }
        catch (Exception e)
        {
            LogShutdownFailed(_logger, _userKey, e);
        }
    }

    // ── Shutdown ────────────────────────────────────────────────────────

    /// <summary>Fleet is stopping: the host gets <c>shutdown</c> (killed after the grace) and nothing starts it again.</summary>
    public async Task ShutdownAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _leaseTimer?.Dispose();
            _leaseTimer = null;
        }

        // A step in flight ends soon with the lifetime cancelled; don't wait on one that won't.
        var gated = await _gate.WaitAsync(ShutdownGrace + ShutdownGrace).ConfigureAwait(false);
        try
        {
            CancelBackoff();
            _watch?.Dispose();
            _watch = null;
            if (_run is { } run)
                await StopRunAsync(run).ConfigureAwait(false);
            DeleteFolder(StagedRoot);
            Enter(ModHostStates.Stopped, "Fleet is shutting down.");
        }
        finally
        {
            if (gated)
                _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // ── $ calls ─────────────────────────────────────────────────────────

    public async Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (!Calls.Contains(method))
            throw new ModHostRpcException(ModHostErrorCodes.MethodNotFound, $"method not found: {method}");

        var modId = Text(parameters, "mod");
        var sessionId = Text(parameters, "sessionId");
        if (modId is null || sessionId is null)
            throw Invalid($"{method} needs a mod and a sessionId");
        var route = _routes.FirstOrDefault(r => string.Equals(r.Id, modId, StringComparison.Ordinal))
                    ?? throw Invalid($"unknown mod {modId}");
        if (route.DraftSessionId is { } own && !string.Equals(own, sessionId, StringComparison.Ordinal))
            throw Invalid($"{modId} runs only in its own session");

        // A draft shares the store of the kept mod with its name.
        var name = route.Name;
        switch (method)
        {
            case "store.get":
            {
                var value = await _store.GetValueAsync(UserId, name, StoreKey(parameters), ct).ConfigureAwait(false);
                return Answer(w =>
                {
                    if (value is { } found)
                    {
                        w.WritePropertyName("value");
                        found.WriteTo(w);
                    }
                });
            }
            case "store.set":
            {
                var key = StoreKey(parameters);
                if (!parameters.TryGetProperty("value", out var value))
                    throw Invalid("store.set needs a value");
                try
                {
                    await _store.SetValueAsync(UserId, name, key, value, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is ModStoreFullException or ArgumentException)
                {
                    throw Invalid(e.Message);
                }
                return EmptyAnswer;
            }
            case "store.delete":
                await _store.DeleteValueAsync(UserId, name, StoreKey(parameters), ct).ConfigureAwait(false);
                return EmptyAnswer;
            case "store.keys":
            {
                var keys = await _store.KeysAsync(UserId, name, ct).ConfigureAwait(false);
                return Answer(w =>
                {
                    w.WriteStartArray("keys");
                    foreach (var key in keys)
                        w.WriteStringValue(key);
                    w.WriteEndArray();
                });
            }
            case "session.get":
            {
                var session = await _deps.FindSession(UserId, sessionId, ct).ConfigureAwait(false);
                if (session is null || !string.Equals(session.UserId, UserId, StringComparison.Ordinal))
                    throw Invalid("unknown session");
                var surfaces = _deps.Ui.SurfacesOf(UserId, sessionId);
                return Answer(w =>
                {
                    w.WriteString("id", session.Id);
                    w.WriteString("title", session.Title);
                    w.WriteString("harness", session.HarnessType);
                    w.WriteString("cwd", session.Directory);
                    w.WriteStartArray("surfaces");
                    foreach (var surface in surfaces)
                        w.WriteStringValue(surface);
                    w.WriteEndArray();
                });
            }
            case "ui.open":
                await _deps.Ui.OpenPaneAsync(UserId, modId, sessionId, Required(parameters, "id"), Text(parameters, "title"), ct).ConfigureAwait(false);
                return EmptyAnswer;
            case "ui.close":
                await _deps.Ui.ClosePaneAsync(UserId, modId, sessionId, Required(parameters, "id"), ct).ConfigureAwait(false);
                return EmptyAnswer;
            default:
            {
                int? timeoutMs = parameters.TryGetProperty("timeoutMs", out var timeout) && timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out var ms) ? ms : null;
                await _deps.Ui.ToastAsync(UserId, modId, sessionId, Required(parameters, "text"), timeoutMs, Text(parameters, "tone"), ct).ConfigureAwait(false);
                return EmptyAnswer;
            }
        }
    }

    public void HandleNotification(string method, JsonElement parameters)
    {
        try
        {
            if (Text(parameters, "mod") is not { } modId)
                return;
            var sessionId = Text(parameters, "sessionId");
            switch (method)
            {
                case "running":
                    if (Text(parameters, "event") is { } running && sessionId is not null && _run is { } run)
                        run.Running[(sessionId, running)] = (modId, run.NextRunning());
                    break;
                case "invalidate":
                    _deps.Signals.Invalidated(UserId, modId, sessionId);
                    break;
                case "log":
                    if (Text(parameters, "text") is { } text)
                    {
                        var level = Text(parameters, "level") is { } given && given is "info" or "warn" or "error" ? given : "info";
                        _deps.Log.Add(UserId, modId, new ModLogLine(Now, level, text, sessionId));
                    }
                    break;
                case "failed":
                    if (Text(parameters, "event") is { } failedEvent
                        && Text(parameters, "kind") is { } kind
                        && Text(parameters, "message") is { } message
                        && parameters.TryGetProperty("strikes", out var strikes)
                        && strikes.ValueKind == JsonValueKind.Number
                        && strikes.TryGetInt32(out var hostStrikes))
                    {
                        Failed(modId, failedEvent, kind, message, hostStrikes, sessionId);
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            LogNotificationFailed(_logger, method, e);
        }
    }

    private static string? Text(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object
           && parameters.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Required(JsonElement parameters, string name)
        => Text(parameters, name) ?? throw Invalid($"\"{name}\" is required");

    private static string StoreKey(JsonElement parameters)
        => Text(parameters, "key") is { } key && ModNames.IsValidStoreKey(key)
            ? key
            : throw Invalid("a store key is letters, digits, _, - and ., 1 to 64");

    private static ModHostRpcException Invalid(string message) => new(ModHostErrorCodes.InvalidParams, message);

    /// <summary>An object answer, written directly: no reflection, so it trims.</summary>
    private static JsonElement Answer(Action<Utf8JsonWriter> write)
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

    // ── Types ───────────────────────────────────────────────────────────

    /// <param name="Number">A kept mod's version; null for a draft.</param>
    /// <param name="SessionId">A draft's session; null for a kept mod.</param>
    private sealed record WantedMod(string Id, string Name, int? Number, string? SessionId);

    /// <param name="StagedFolder">A draft's staged copy (its generation folder), deleted when it's replaced or unloaded.</param>
    private sealed record LoadedMod(ModRoute Route, string? StagedFolder);

    /// <summary>One host process, from start to exit.</summary>
    private sealed class HostRun(IModHostConnection connection, BunLocation bun, DateTimeOffset startedAt)
    {
        private int _hangClaimed;
        private long _runningCount;

        public IModHostConnection Connection { get; } = connection;

        public BunLocation Bun { get; } = bun;

        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>Fleet asked it to stop: its exit isn't a crash.</summary>
        public volatile bool Planned;

        /// <summary>It stopped answering and Fleet killed it.</summary>
        public volatile bool Hung;

        /// <summary>
        /// The mod the host last said was running, per session and event (the <c>running</c> notification), numbered in
        /// the order they came.
        /// </summary>
        public ConcurrentDictionary<(string SessionId, string Event), (string Mod, long Sequence)> Running { get; } = new();

        /// <summary>How many <c>running</c> notifications have come: a dispatch counts only those after it was sent.</summary>
        public long RunningCount => Interlocked.Read(ref _runningCount);

        public long NextRunning() => Interlocked.Increment(ref _runningCount);

        /// <summary>True for the first dispatch to time out on this host only.</summary>
        public bool ClaimHang() => Interlocked.Exchange(ref _hangClaimed, 1) == 0;
    }

    // ── Logging (never the user id: the key is its hash) ────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Mod host for {UserKey} started: pid={ProcessId} bun={BunPath}")]
    private static partial void LogStarted(ILogger logger, string userKey, int processId, string bunPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mod host for {UserKey} stopped: {Reason}")]
    private static partial void LogStopped(ILogger logger, string userKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod host for {UserKey} isn't ready: {Reason}")]
    private static partial void LogNotReady(ILogger logger, string userKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod host for {UserKey} didn't start")]
    private static partial void LogStartFailed(ILogger logger, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod host for {UserKey} {Reason}; restarting in {Delay}")]
    private static partial void LogExited(ILogger logger, string userKey, string reason, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod host for {UserKey} failed while loading or unloading; killing it so it restarts")]
    private static partial void LogHostFailed(ILogger logger, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod host for {UserKey} didn't answer {Event} within 15 s; struck {Struck}")]
    private static partial void LogHung(ILogger logger, string userKey, string @event, string struck);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mod {ModId} didn't load for {UserKey}: {Message}")]
    private static partial void LogLoadRefused(ILogger logger, string modId, string userKey, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mod host didn't unload {ModId}: {Message}")]
    private static partial void LogUnloadRefused(ILogger logger, string modId, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispatch of {Event} for {UserKey} didn't complete")]
    private static partial void LogDispatchFailed(ILogger logger, string @event, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record three strikes for {ModId} ({UserKey})")]
    private static partial void LogStrikeRecordFailed(ILogger logger, string modId, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bun is gone for {UserKey}; the running mod host keeps running")]
    private static partial void LogBunMissing(ILogger logger, string userKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mod host for {UserKey} moves to Bun {BunPath}")]
    private static partial void LogMovingToBun(ILogger logger, string userKey, string bunPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't prune older Bun versions")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mod host for {UserKey} didn't stop cleanly")]
    private static partial void LogShutdownFailed(ILogger logger, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mod host for {UserKey} didn't forget a session")]
    private static partial void LogForgetFailed(ILogger logger, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete a staged mod copy for {UserKey}")]
    private static partial void LogDeleteFailed(ILogger logger, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A mod host step for {UserKey} failed")]
    private static partial void LogStepFailed(ILogger logger, string userKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mod host notification {Method} couldn't be handled")]
    private static partial void LogNotificationFailed(ILogger logger, string method, Exception exception);
}
