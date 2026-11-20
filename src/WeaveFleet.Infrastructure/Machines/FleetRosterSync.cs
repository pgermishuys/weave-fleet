using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// Runs <see cref="FleetRoster"/> in the background (Your Fleet): when this machine's list changes it compares
/// lists with every machine in it, and when a machine answers again it compares lists with that one, so a machine that
/// was off catches up. Local mode only.
/// </summary>
public sealed partial class FleetRosterSync(
    RemoteMachineService machines,
    FleetRoster roster,
    RemoteMachineWatcher watcher,
    FleetOptions options,
    ILogger<FleetRosterSync> logger) : IHostedService, IDisposable
{
    internal static readonly TimeSpan PullEvery = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _stopping = new();
    private int _running;
    private int _again;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Auth.Enabled || !options.Auth.TokenAuthEnabled)
            return Task.CompletedTask;

        var previous = watcher.MachineConnected;
        watcher.MachineConnected = async id =>
        {
            if (previous is not null)
                await previous(id);
            await RunSafelyAsync(() => roster.SyncWithAsync(id, _stopping.Token));
        };
        machines.Changed += OnChanged;
        roster.SyncRequested += Trigger;
        _ = Task.Run(PullPeriodicallyAsync, CancellationToken.None);
        // Whatever changed while this machine was off.
        Trigger();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    public Task StopAsync(CancellationToken cancellationToken)
    {
        machines.Changed -= OnChanged;
        roster.SyncRequested -= Trigger;
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The container disposed this first; nothing is running then.
        }

        return Task.CompletedTask;
    }

    private void OnChanged(string _) => Trigger();

    // A machine nobody can reach hears nothing from the others, so it looks for what the others gained on its own, once a minute.
    private async Task PullPeriodicallyAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PullEvery);
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                if (!await roster.HasOwnAddressAsync(_stopping.Token))
                    Trigger();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    // One pass at a time; a change during a pass earns one more.
    private void Trigger()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Volatile.Write(ref _again, 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Volatile.Write(ref _again, 0);
                    await Task.Delay(500, _stopping.Token);
                    await RunSafelyAsync(() => roster.SyncAllAsync(_stopping.Token));
                }
                while (Volatile.Read(ref _again) == 1 && !_stopping.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
    }

    private async Task RunSafelyAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comparing machine lists (Your Fleet) failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
