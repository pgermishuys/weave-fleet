using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Push;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// Makes this machine a phone's "home": it listens to every machine in its list and pushes their notifications to
/// this machine's push subscriptions, tagged with the machine they came from. One <see cref="RemoteMachineConnection"/>
/// per machine, rebuilt when the list changes. Notifications that say they're from this machine are dropped, so two
/// machines listing each other don't echo. Status (online, unreachable, unauthorized) goes on the machine's row for the
/// phone's inbox. Local mode only.
/// <para>
/// It also follows sessions on those machines that a session here waits to hear from (<see cref="IRemoteSessionEvents"/>):
/// their events go to <see cref="RemoteSessionTurns"/>, kept across reconnects and list changes, until nobody waits or
/// the wait's lifetime runs out.
/// </para>
/// </summary>
public sealed partial class RemoteMachineWatcher(
    RemoteMachineService machines,
    PushNotificationDispatcher dispatcher,
    MachineIdentityStore identity,
    FleetOptions options,
    ILoggerFactory loggerFactory,
    DeviceGrantService? grants = null,
    RemoteSessionTurns? turns = null,
    TimeProvider? time = null) : IHostedService, IAsyncDisposable, IRemoteSessionEvents
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RemoteMachineWatcher>();
    private readonly ConcurrentDictionary<string, RemoteMachineConnection> _connections = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // (machine id, session id) → when it was first followed
    private readonly ConcurrentDictionary<(string MachineId, string SessionId), DateTimeOffset> _followed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Called each time a machine answers again: work that waited for it (removing a phone there) can go.</summary>
    public Func<string, Task>? MachineConnected { get; set; } = grants is null
        ? null
        : machineId => grants.RetryRevocationsAsync(machineId, CancellationToken.None);

    /// <summary>For tests: how requests to other machines are sent (a test server's handler).</summary>
    internal HttpMessageHandler? Handler { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Auth.Enabled || !options.Auth.TokenAuthEnabled)
            return;

        machines.Changed += OnChanged;
        foreach (var machine in await machines.ListAsync())
            await ConnectAsync(machine);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        machines.Changed -= OnChanged;
        foreach (var id in _connections.Keys.ToList())
            await DisconnectAsync(id);
    }

    /// <inheritdoc />
    public void Follow(string machineId, string sessionId)
    {
        if (turns is null)
            return;

        // Waits that ran out stop being followed; a turn there that never ends would otherwise keep its topic for good.
        var cutoff = _time.GetUtcNow() - SessionUpdates.WatchLifetime;
        foreach (var (key, since) in _followed)
        {
            if (since < cutoff)
                Unfollow(key.MachineId, key.SessionId);
        }

        if (_followed.TryAdd((machineId, sessionId), _time.GetUtcNow()) && _connections.TryGetValue(machineId, out var connection))
            _ = Run(() => connection.FollowAsync(sessionId), machineId);
    }

    /// <summary>The sessions followed on <paramref name="machineId"/>.</summary>
    internal IReadOnlyCollection<string> FollowedOn(string machineId)
        => [.. _followed.Keys.Where(key => key.MachineId == machineId).Select(key => key.SessionId)];

    private void Unfollow(string machineId, string sessionId)
    {
        if (_followed.TryRemove((machineId, sessionId), out _) && _connections.TryGetValue(machineId, out var connection))
            _ = Run(() => connection.UnfollowAsync(sessionId), machineId);
    }

    private Task OnSessionEventAsync(string machineId, string sessionId, DomainEvent domainEvent)
    {
        if (turns is not null && _followed.ContainsKey((machineId, sessionId)) && turns.Observe(sessionId, domainEvent))
            Unfollow(machineId, sessionId);
        return Task.CompletedTask;
    }

    private async Task OnFollowingAsync(string machineId, string sessionId)
    {
        if (turns is not null && await turns.CatchUpAsync(machineId, sessionId))
            Unfollow(machineId, sessionId);
    }

    private async Task Run(Func<Task> work, string machineId)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            LogFollowFailed(_logger, ex, machineId);
        }
    }

    /// <summary>Whether a connection to <paramref name="machineId"/> is being kept.</summary>
    internal bool IsWatching(string machineId) => _connections.ContainsKey(machineId);

    private void OnChanged(string machineId) => _ = ReconcileAsync(machineId);

    private async Task ReconcileAsync(string machineId)
    {
        try
        {
            await DisconnectAsync(machineId);
            var machine = await machines.GetAsync(machineId);
            if (machine is not null)
                await ConnectAsync(machine);
        }
        catch (Exception ex)
        {
            LogReconcileFailed(_logger, ex, machineId);
        }
    }

    private async Task ConnectAsync(RemoteMachine machine)
    {
        if (machine.Id == identity.Get().Id)
            return;

        string token;
        try
        {
            token = machines.TokenOf(machine);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // Encrypted with keys this Fleet no longer has: the owner has to give the token again.
            await machines.SetStatusAsync(machine.Id, RemoteMachineStatuses.Unauthorized, seen: false);
            return;
        }

        await _gate.WaitAsync();
        try
        {
            var connection = new RemoteMachineConnection(
                machine,
                token,
                payload => ForwardAsync(machine, payload),
                (status, seen) => machines.SetStatusAsync(machine.Id, status, seen),
                () => MachineConnected?.Invoke(machine.Id) ?? Task.CompletedTask,
                loggerFactory.CreateLogger<RemoteMachineConnection>(),
                Handler,
                () => FollowedOn(machine.Id),
                (sessionId, domainEvent) => OnSessionEventAsync(machine.Id, sessionId, domainEvent),
                sessionId => OnFollowingAsync(machine.Id, sessionId));
            if (!_connections.TryAdd(machine.Id, connection))
            {
                await connection.DisposeAsync();
                return;
            }

            connection.Start();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectAsync(string machineId)
    {
        if (_connections.TryRemove(machineId, out var connection))
            await connection.DisposeAsync();
    }

    private Task ForwardAsync(RemoteMachine machine, SessionNotificationPayload payload)
    {
        // A machine's hub only carries its own notifications. One that claims to be another machine's is dropped: the
        // id decides which of the phone's keys an Allow from the notification uses. That also stops two machines that
        // list each other from echoing this machine's own notifications back.
        if (payload.MachineId is not null && payload.MachineId != machine.Id)
            return Task.CompletedTask;
        if (machine.Id == identity.Get().Id)
            return Task.CompletedTask;

        dispatcher.Enqueue(payload with
        {
            MachineId = machine.Id,
            MachineName = machine.Name,
            Kind = payload.Kind ?? PushNotificationDispatcher.KindFromReason(payload.Reason),
        });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys.ToList())
            await DisconnectAsync(id);
        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't follow a session on machine {MachineId}.")]
    private static partial void LogFollowFailed(ILogger logger, Exception ex, string machineId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the connection to machine {MachineId}.")]
    private static partial void LogReconcileFailed(ILogger logger, Exception ex, string machineId);
}
