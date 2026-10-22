using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Updates;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>Whether this Fleet can restart itself into a staged update, and if not, why (shown on the Updates page).</summary>
public sealed record RestartCapability(bool CanRestart, string? Reason);

public enum RestartOutcome { Accepted, NothingStaged, Busy, CannotRestart }

/// <param name="Working">How many sessions were in a turn, when the answer is <see cref="RestartOutcome.Busy"/>.</param>
public sealed record RestartResult(RestartOutcome Outcome, string? Message = null, string? To = null, int Working = 0);

/// <summary>
/// Restarts this Fleet into the update it has staged: exits with <see cref="ExitCode"/> after a graceful stop, and the
/// launcher (or a service that restarts on any exit) starts it again, applying the update first. Also ends the trial of
/// a version that has just been applied once the host is up, and reports one that failed to start.
/// </summary>
public sealed partial class UpdateRestartService(
    FleetOptions options,
    UpdateFiles files,
    UpdateDownloadService downloads,
    UpdateRestartGate gate,
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<UpdateRestartService> logger,
    // The rest are for tests: how the process is told its exit code, what its environment says, what is staged, and how many sessions work.
    Action<int>? setExitCode = null,
    Func<string, string?>? environment = null,
    Func<string?>? stagedVersion = null,
    Func<Task<MachineSessionCounts>>? workingSessions = null,
    Func<string?>? readCgroup = null,
    // Where the reason for a restart is written down before exiting, so the new version can say what happened.
    UpdateRuleStore? rules = null) : IHostedService
{
    /// <summary>"Restart to update": the launcher starts itself again on it. 75 (another Fleet) and 1 (errors) are taken.</summary>
    public const int ExitCode = 85;

    /// <summary>The launcher sets this to <see cref="ExitCode"/> when it will restart the app on that exit code.</summary>
    public const string LauncherVariable = "FLEET_LAUNCHER_RESTART";

    /// <summary>Time for the response that asked for the restart to reach whoever asked.</summary>
    private static readonly TimeSpan s_replyGrace = TimeSpan.FromMilliseconds(500);

    private readonly Action<int> _setExitCode = setExitCode ?? (code => Environment.ExitCode = code);
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Migrations have run and Kestrel listens by now: the new version is healthy, so the launcher may keep it.
        lifetime.ApplicationStarted.Register(() =>
        {
            if (files.ConfirmTrial(CurrentVersion))
                LogTrialConfirmed(CurrentVersion);
            rules?.CompleteUpdate(CurrentVersion);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The reader is scoped (it holds scoped repositories), so a singleton resolves it per use.
    private async Task<MachineSessionCounts> ReadWorkingAsync()
    {
        using var scope = scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MachineCapabilitiesReader>().ReadAsync()).Sessions;
    }

    private static string CurrentVersion => FleetInstrumentation.ServiceVersion.Split('+')[0].TrimStart('v');

    /// <summary>The update that failed to start here and was undone, until a later one succeeds.</summary>
    public UpdateFailure? RolledBack() => files.ReadFailure();

    public RestartCapability Capability()
    {
        if (options.Desktop.Enabled)
            return new RestartCapability(false, "The desktop app installs its own updates.");
        if (!files.IsInstalled)
            return new RestartCapability(false, "Not an installed Fleet, so it can't restart itself.");
        if (_environment(LauncherVariable) == ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || RunsInNodeUnit()
            || (_environment("XPC_SERVICE_NAME") ?? "").StartsWith("io.tryweave.fleet", StringComparison.Ordinal))
            return new RestartCapability(true, null);

        return new RestartCapability(false, "Restart Fleet once by hand; after that it can restart itself.");
    }

    /// <summary>
    /// Whether this process runs inside the fleet-node systemd unit (which restarts it on any exit). INVOCATION_ID can't
    /// say: a terminal started as a systemd unit or scope hands it to every shell in it.
    /// </summary>
    private bool RunsInNodeUnit()
    {
        string? cgroup;
        try
        {
            cgroup = (readCgroup ?? (() => OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/cgroup") : null))();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return cgroup is not null
            && cgroup.Split('\n').Any(line => line.Split('/').Any(segment => segment.Trim() == "fleet-node.service"));
    }

    /// <summary>The version downloaded and newer than this one, or null.</summary>
    public string? PendingUpdate()
        => (stagedVersion ?? downloads.StagedVersion)() is { } staged && UpdateCheckService.IsNewerVersion(staged, CurrentVersion) ? staged : null;

    /// <summary>
    /// Agrees to restart, when it can: something is staged, nothing is in a turn, and it has a way back up.
    /// <paramref name="trigger"/> is what started it (<c>manual</c>, <c>rollout</c>, <c>idle</c> or <c>overnight</c>).
    /// </summary>
    public async Task<RestartResult> BeginAsync(string trigger = "manual")
    {
        var capability = Capability();
        if (!capability.CanRestart)
            return new RestartResult(RestartOutcome.CannotRestart, capability.Reason);

        var staged = PendingUpdate();
        if (staged is null)
            return new RestartResult(RestartOutcome.NothingStaged, "No update is downloaded yet.");

        // Closed first, so a prompt arriving while the sessions are counted is turned away rather than cut off.
        if (!gate.TryClose())
            return new RestartResult(RestartOutcome.Accepted, To: staged);

        // A session stopped on a question or a permission ask is still in its turn: a restart would cut it off.
        var counts = workingSessions is null ? await ReadWorkingAsync() : await workingSessions();
        var working = counts.Busy;
        if (working > 0)
        {
            gate.Reopen();
            return new RestartResult(RestartOutcome.Busy, $"{working} {(working == 1 ? "session is" : "sessions are")} working or waiting for you.", Working: working);
        }

        LogRestarting(CurrentVersion, staged);
        rules?.RecordRestart(new LastUpdate(CurrentVersion, staged, time.GetUtcNow().ToString("O"), trigger));
        _ = Task.Run(async () =>
        {
            await Task.Delay(s_replyGrace, time).ConfigureAwait(false);
            _setExitCode(ExitCode);
            lifetime.StopApplication();
        });
        return new RestartResult(RestartOutcome.Accepted, To: staged);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fleet v{Version} started after an update and is staying.")]
    private partial void LogTrialConfirmed(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restarting from v{From} into v{To}.")]
    private partial void LogRestarting(string from, string to);
}
