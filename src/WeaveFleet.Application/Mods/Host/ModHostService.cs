using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Every user's mod host (<see cref="IModHost"/>): one <see cref="ModHostSupervisor"/> per user, made when first needed.
/// Brings a user's host in line on <c>mods.changed</c>, polls running and not-ready hosts every
/// <see cref="PollInterval"/> (a new Bun, a switch flipped elsewhere), and stops every host when Fleet stops.
/// </summary>
public sealed partial class ModHostService : IModHost, IHostedService, IAsyncDisposable
{
    /// <summary>The user Fleet runs as without sign-in (<c>LocalUserContext</c>): their host is brought up at start-up.</summary>
    public const string LocalUserId = "local-user";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly ModHostDependencies _deps;
    private readonly IEventBroadcaster _events;
    private readonly IReadOnlyList<string> _startupUsers;
    private readonly ConcurrentDictionary<string, ModHostSupervisor> _supervisors = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _poll;
    private Task? _listening;
    private volatile bool _stopped;

    public ModHostService(
        IModHostConnectionFactory connections,
        IModUserGate gate,
        IModStrikeRecorder strikes,
        IModHostUi ui,
        IModHostSignals signals,
        IModHostBun bun,
        IModHostFiles files,
        IModDraftWatcher watcher,
        IModVersionStore store,
        IServiceScopeFactory scopes,
        IBackgroundUserScope users,
        IEventBroadcaster events,
        ModLogBook log,
        TimeProvider time,
        ILogger<ModHostService> logger)
        : this(
            new ModHostDependencies(connections, gate, strikes, ui, signals, bun, files, watcher, store, FindSessionIn(scopes, users), log, time, logger),
            events,
            [LocalUserId])
    {
    }

    internal ModHostService(ModHostDependencies deps, IEventBroadcaster events, IReadOnlyList<string> startupUsers)
    {
        _deps = deps;
        _events = events;
        _startupUsers = startupUsers;
    }

    /// <summary>Sessions are read as their user, in a scope of their own: the host's calls come outside any request.</summary>
    private static ModSessionLookup FindSessionIn(IServiceScopeFactory scopes, IBackgroundUserScope users)
        => async (userId, sessionId, ct) =>
        {
            using var asUser = users.Begin(userId);
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
                return await scope.ServiceProvider.GetRequiredService<ISessionRepository>().GetByIdAsync(sessionId).ConfigureAwait(false);
        };

    internal ModHostSupervisor? SupervisorOf(string userId) => _supervisors.GetValueOrDefault(userId);

    /// <summary>
    /// The user's supervisor. The first time Fleet sees a user, their host is brought in line in the background, once
    /// per Fleet process: a signed-in user's mods run after a restart without waiting for a change.
    /// </summary>
    private ModHostSupervisor For(string userId)
    {
        if (_supervisors.TryGetValue(userId, out var supervisor))
            return supervisor;
        var created = false;
        lock (_sync)
        {
            supervisor = _supervisors.GetOrAdd(userId, id =>
            {
                created = true;
                return new ModHostSupervisor(id, _deps);
            });
        }
        if (created)
            Reconcile(supervisor);
        return supervisor;
    }

    /// <summary>A call that answers from what's there now, and only makes sure a new user's host is on its way.</summary>
    private ModHostSupervisor? Seen(string userId)
    {
        var supervisor = SupervisorOf(userId);
        if (supervisor is null && !_stopped)
            For(userId);
        return supervisor;
    }

    public ModHostStatus GetStatus(string userId)
        => Seen(userId)?.GetStatus() ?? ModHostStatus.StoppedWith(null);

    public Task EnsureAsync(string userId, CancellationToken ct = default)
        => _stopped ? Task.CompletedTask : For(userId).EnsureAsync(ct);

    public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default)
        => _stopped
            ? Task.FromException<JsonElement>(new ModHostNotReadyException("Fleet is shutting down."))
            : For(userId).CheckAsync(folder, ct);

    /// <summary>Never starts a host itself: a user without one gets <see cref="ModDispatchResult.NotDispatched"/>.</summary>
    public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default)
        => Seen(userId)?.DispatchAsync(request, ct) ?? Task.FromResult(ModDispatchResult.NotDispatched);

    public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default)
        => Seen(userId)?.ForgetSessionAsync(sessionId, ct) ?? Task.CompletedTask;

    public ModLoadProblem? GetLoadProblem(string userId, string modId) => Seen(userId)?.GetLoadProblem(modId);

    public IReadOnlyList<ModLogLine> GetLog(string userId, string modId)
    {
        Seen(userId);
        return _deps.Log.Read(userId, modId);
    }

    // ── Hosting ─────────────────────────────────────────────────────────

    /// <summary>Starts listening and polling, and brings the local user's host up in the background: never blocks start-up.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listening = Task.Run(() => ListenAsync(_stopping.Token), CancellationToken.None);
        _poll = _deps.Time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);
        // Also when a call saw the user before Fleet finished starting.
        foreach (var userId in _startupUsers)
            Reconcile(For(userId));
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.SubscribeAsync(["sessions"], null, ct).ConfigureAwait(false))
            {
                if (_stopped || e.Type != EventTypes.ModsChanged || string.IsNullOrEmpty(e.UserId))
                    continue;
                var supervisor = For(e.UserId);
                supervisor.ForgetRefusals();
                Reconcile(supervisor);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            LogListenFailed(_deps.Logger, e);
        }
    }

    private void Poll()
    {
        if (_stopped)
            return;
        foreach (var supervisor in _supervisors.Values)
        {
            if (supervisor.GetStatus().State is ModHostStates.Running or ModHostStates.NotReady)
                Reconcile(supervisor);
        }
    }

    /// <summary>Off the caller's thread: a reconcile can wait on a host starting.</summary>
    private void Reconcile(ModHostSupervisor supervisor)
    {
        if (!_stopped)
            _ = Task.Run(() => supervisor.EnsureAsync(), CancellationToken.None);
    }

    /// <summary>Every host gets <c>shutdown</c> at once (each is killed after its grace); nothing restarts after.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_poll is not null)
            await _poll.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_supervisors.Values.Select(s => s.ShutdownAsync())).ConfigureAwait(false);
        if (_listening is { } listening)
            await listening.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var supervisor in _supervisors.Values)
            await supervisor.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The mod host service stopped listening for mods.changed")]
    private static partial void LogListenFailed(ILogger logger, Exception exception);
}
