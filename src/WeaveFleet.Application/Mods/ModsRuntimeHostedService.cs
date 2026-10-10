using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// Keeps the Bun mods run on installed and up to date without anyone asking: a few seconds after start, whenever Fleet
/// wants another release, and every <c>Fleet:Update:CheckIntervalHours</c> (which is also the retry for a security update
/// that failed). <see cref="IModsRuntime.EvaluateAsync"/> decides whether anything needs installing.
/// </summary>
public sealed partial class ModsRuntimeHostedService(
    IModsRuntime runtime,
    IBunReleases releases,
    FleetOptions options,
    ILogger<ModsRuntimeHostedService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _releaseChanged = new(0);

    /// <summary>Test seam: how long after start the first check waits, so Fleet finishes starting first.</summary>
    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: the time between periodic checks; the default is <c>Fleet:Update:CheckIntervalHours</c>, and none when that is 0.</summary>
    internal TimeSpan? Interval { get; init; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        releases.Changed += OnReleaseChanged;
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            await EvaluateAsync(release: false, stoppingToken).ConfigureAwait(false);

            var hours = options.Update.CheckIntervalHours;
            var interval = Interval ?? (hours > 0 ? TimeSpan.FromHours(hours) : Timeout.InfiniteTimeSpan);
            while (!stoppingToken.IsCancellationRequested)
            {
                var changed = await _releaseChanged.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                await EvaluateAsync(release: changed, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Fleet is stopping.
        }
        finally
        {
            releases.Changed -= OnReleaseChanged;
        }
    }

    private void OnReleaseChanged(object? sender, BunReleaseChangedEventArgs e) => _releaseChanged.Release();

    private async Task EvaluateAsync(bool release, CancellationToken ct)
    {
        try
        {
            if (release)
                await runtime.ReleaseChangedAsync(ct).ConfigureAwait(false);
            else
                await runtime.EvaluateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEvaluateFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking whether the mod runtime needs installing failed.")]
    private partial void LogEvaluateFailed(Exception ex);
}
