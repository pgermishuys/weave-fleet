using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Harnesses;

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
        IEventBroadcaster events,
        TimeProvider time,
        ILogger<ModHostService> logger)
    {
        _deps = new ModHostDependencies(options, connections, gate, bun, files, store, time, logger);
        _events = events;
        _logger = logger;
    }

    internal ModHostSupervisor? SupervisorOf(string userId) => _supervisors.GetValueOrDefault(userId);

    public ModHostStatus GetStatus(string userId) => SupervisorOf(userId)?.GetStatus() ?? ModHostStatus.Stopped;

    public Task EnsureAsync(string userId, CancellationToken ct = default)
        => Volatile.Read(ref _stop) is not null ? Task.CompletedTask : _supervisors.GetOrAdd(userId, id => new ModHostSupervisor(id, _deps)).EnsureAsync(ct);

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
                    EnsureInBackground(userId);
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
