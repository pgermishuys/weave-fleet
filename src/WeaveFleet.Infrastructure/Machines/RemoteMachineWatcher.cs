using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Push;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// Makes this machine a phone's "home": it listens to every machine in its list and pushes their notifications to
/// this machine's push subscriptions, tagged with the machine they came from. One <see cref="RemoteMachineConnection"/>
/// per machine, rebuilt when the list changes. Notifications that say they're from this machine are dropped, so two
/// machines listing each other don't echo. Status (online, unreachable, unauthorized) goes on the machine's row for the
/// phone's inbox. Local mode only.
/// </summary>
public sealed partial class RemoteMachineWatcher(
    RemoteMachineService machines,
    PushNotificationDispatcher dispatcher,
    MachineIdentityStore identity,
    FleetOptions options,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RemoteMachineWatcher>();
    private readonly ConcurrentDictionary<string, RemoteMachineConnection> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Called each time a machine answers again: work that waited for it (removing a phone there) can go.</summary>
    public Func<string, Task>? MachineConnected { get; set; }

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
                Handler);
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
        // Never push this machine's own notifications twice, nor bounce them between machines that list each other.
        if (payload.MachineId == identity.Get().Id)
            return Task.CompletedTask;

        dispatcher.Enqueue(payload with
        {
            MachineId = payload.MachineId ?? machine.Id,
            MachineName = payload.MachineName ?? machine.Name,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the connection to machine {MachineId}.")]
    private static partial void LogReconcileFailed(ILogger logger, Exception ex, string machineId);
}
