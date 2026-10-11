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
/// Every user's mod host (<see cref="IModHost"/>): one <see cref="ModHostSupervisor"/> per user. A user's host is brought in
/// line when <c>mods.changed</c> says their mods did, and everyone's is shut down when Fleet stops.
/// </summary>
public sealed partial class ModHostService : IModHost, IHostedService, IAsyncDisposable
{
    /// <summary>The user Fleet runs as without sign-in: their host is brought up at start-up.</summary>
    public const string LocalUserId = "local-user";

    private readonly ModHostDependencies _deps;
    private readonly IEventBroadcaster _events;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ModHostSupervisor> _supervisors = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _sync = new();
    private Task? _listening;
    private Task? _stop;

    public ModHostService(
        ModHostOptions options,
        IModHostConnectionFactory connections,
        IModUserGate gate,
        IModHostBun bun,
        IModHostFiles files,
        IModVersionStore store,
        IModStrikeRecorder strikes,
        IModHostUi ui,
        IServiceScopeFactory scopes,
        IBackgroundUserScope users,
        IEventBroadcaster events,
        TimeProvider time,
        ILogger<ModHostService> logger)
    {
        _deps = new ModHostDependencies(options, connections, gate, bun, files, store, strikes, ui, FindSessionIn(scopes, users), time, logger);
        _events = events;
        _logger = logger;
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

    private bool Stopping => Volatile.Read(ref _stop) is not null;

    private ModHostSupervisor For(string userId) => _supervisors.GetOrAdd(userId, id => new ModHostSupervisor(id, _deps));

    public ModHostStatus GetStatus(string userId) => SupervisorOf(userId)?.GetStatus() ?? ModHostStatus.Stopped;

    public Task EnsureAsync(string userId, CancellationToken ct = default) => Stopping ? Task.CompletedTask : For(userId).EnsureAsync(ct);

    public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default)
        => SupervisorOf(userId)?.DispatchAsync(request, ct) ?? Task.FromResult(ModDispatchResult.NotDispatched);

    public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default)
        => Stopping ? Task.FromException<JsonElement>(new ModHostNotReadyException("Fleet is stopping.")) : For(userId).CheckAsync(folder, ct);

    public Task ReloadDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default)
        => Stopping ? Task.CompletedTask : For(userId).ReloadDraftAsync(sessionId, name, ct);

    public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default)
        => SupervisorOf(userId)?.ForgetSessionAsync(sessionId) ?? Task.CompletedTask;

    public ModLoadProblem? GetLoadProblem(string userId, string modId) => SupervisorOf(userId)?.GetLoadProblem(modId);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listening = ListenAsync(_stopping.Token);
        EnsureInBackground(LocalUserId);
        return Task.CompletedTask;
    }

    /// <summary>Stops listening and shuts every host down; runs once, later calls wait for the same stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
            return _stop ??= StopCoreAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task StopCoreAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_listening is { } listening)
            await listening.ConfigureAwait(false);
        await Task.WhenAll(_supervisors.Values.Select(s => s.ShutdownAsync())).ConfigureAwait(false);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.SubscribeAsync(["sessions"], null, ct).ConfigureAwait(false))
            {
                if (e is { Type: EventTypes.ModsChanged, UserId: { } userId })
                {
                    SupervisorOf(userId)?.ClearLoadProblems();
                    EnsureInBackground(userId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogListenFailed(ex);
        }
    }

    private void EnsureInBackground(string userId) => _ = EnsureLoggedAsync(userId);

    private async Task EnsureLoggedAsync(string userId)
    {
        try
        {
            await EnsureAsync(userId, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogEnsureFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet stopped listening for changes to mods")]
    private partial void LogListenFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bringing a mod host in line with its user's mods failed")]
    private partial void LogEnsureFailed(Exception ex);
}
