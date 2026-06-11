using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Best-effort startup warmup for the pooled OpenCode harness.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Local / auth-disabled mode:</strong> Warms exactly one pooled OpenCode process using
/// the deterministic local owner sentinel <c>"local-user"</c> (matching
/// <see cref="LocalUserContext"/>). The warmup is opportunistic — if it fails for any reason the
/// exception is swallowed and the application continues to boot normally.
/// </para>
/// <para>
/// <strong>Auth-enabled / hosted mode:</strong> Skips warmup entirely. There is no trusted acting
/// user available at startup (no request principal, no authenticated context) so attempting
/// credential resolution would produce incorrect or empty credentials. Post-login and
/// post-preference warmup is covered by the runtime warmup API
/// (<c>POST /api/harnesses/opencode/warmup</c>) which operates within an authenticated request
/// context.
/// </para>
/// <para>
/// The service must be registered <em>after</em> startup recovery (mark-all-stopped) so the
/// process pool is not pre-warmed before the database is in a consistent state.
/// </para>
/// </remarks>
public sealed partial class OpenCodeWarmupHostedService : IHostedService
{
    private const string LocalOwnerUserId = "local-user";

    private readonly IHarnessRegistry _harnessRegistry;
    private readonly FleetOptions _options;
    private readonly ILogger<OpenCodeWarmupHostedService> _logger;

    public OpenCodeWarmupHostedService(
        IHarnessRegistry harnessRegistry,
        FleetOptions options,
        ILogger<OpenCodeWarmupHostedService> logger)
    {
        _harnessRegistry = harnessRegistry;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Auth-enabled: no trusted user context at startup — skip and let the runtime
        // warmup API handle warming after the first authenticated request.
        if (_options.Auth.Enabled)
        {
            LogWarmupSkippedAuthEnabled(_logger);
            return;
        }

        var runtime = _harnessRegistry.GetRuntimeByType("opencode");
        if (runtime is null)
        {
            LogWarmupSkippedNoRuntime(_logger);
            return;
        }

        try
        {
            var warmed = await runtime.WarmupPooledInstanceAsync(LocalOwnerUserId, cancellationToken)
                .ConfigureAwait(false);

            if (warmed)
                LogWarmupCompleted(_logger);
            else
                LogWarmupSkippedPooledModeDisabled(_logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application is shutting down before warmup completed — not an error.
            LogWarmupCancelled(_logger);
        }
        catch (Exception ex)
        {
            // Best-effort: any failure is logged but must not block application boot.
            LogWarmupFailed(_logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OpenCode startup warmup skipped: auth-enabled mode has no trusted user context at startup. " +
                  "Warmup will occur via POST /api/harnesses/opencode/warmup after the first authenticated request.")]
    private static partial void LogWarmupSkippedAuthEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "OpenCode startup warmup skipped: OpenCode runtime not registered.")]
    private static partial void LogWarmupSkippedNoRuntime(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "OpenCode startup warmup skipped: pooled mode is not enabled for the local owner.")]
    private static partial void LogWarmupSkippedPooledModeDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OpenCode startup warmup completed: pooled process pre-warmed for local owner.")]
    private static partial void LogWarmupCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "OpenCode startup warmup cancelled: application shutting down before warmup completed.")]
    private static partial void LogWarmupCancelled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OpenCode startup warmup failed (best-effort); application boot continues normally.")]
    private static partial void LogWarmupFailed(ILogger logger, Exception ex);
}
