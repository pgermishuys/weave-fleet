using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Hands every change from <see cref="IAppRunner"/> to <see cref="AppRunRecorder"/>, one at a time and in the
/// order they happened. Before listening, it cleans up after the previous Fleet.
/// </summary>
internal sealed partial class AppRunRecorderService(
    IAppRunner apps,
    IServiceScopeFactory scopes,
    ILogger<AppRunRecorderService> logger) : BackgroundService
{
    private readonly Channel<AppRunChange> _changes = Channel.CreateUnbounded<AppRunChange>(new UnboundedChannelOptions { SingleReader = true });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var killed = await scope.ServiceProvider.GetRequiredService<AppRunRecorder>().CleanUpAfterPreviousFleetAsync();
            if (killed > 0)
                LogLeftoversKilled(logger, killed);
        }
        catch (Exception ex)
        {
            LogCleanupFailed(logger, ex);
        }

        apps.Changed += OnChanged;
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        apps.Changed -= OnChanged;
        _changes.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AppRunRecorder>().RecordAsync(change, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecordFailed(logger, ex, change.App.Id);
            }
        }
    }

    private void OnChanged(AppRunChange change) => _changes.Writer.TryWrite(change);

    [LoggerMessage(Level = LogLevel.Information, Message = "Killed {Count} app process trees left running by a previous Fleet")]
    private static partial void LogLeftoversKilled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up apps left by a previous Fleet failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording a change to app {AppId} failed")]
    private static partial void LogRecordFailed(ILogger logger, Exception ex, string appId);
}
