using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// Installs the Bun mods run on and tells clients how it's going: one install at a time, in the background, apart from
/// the request that started it. A first install that fails or is cancelled turns the Mods switch back off for the user
/// who started it, unless a Bun is usable after all.
/// </summary>
public sealed partial class ModsRuntime(
    IBunRuntime bun,
    IBunReleases releases,
    IEventBroadcaster events,
    IServiceScopeFactory scopes,
    IBackgroundUserScope users,
    FleetOptions options,
    TimeProvider clock,
    ILogger<ModsRuntime> logger) : IModsRuntime
{
    private readonly object _sync = new();
    private Install? _running;
    private ModsRuntimeJob? _job;
    private Task _sends = Task.CompletedTask;

    /// <summary>Test seam: the user's home folder, shortened to <c>~</c> in paths shown to the user.</summary>
    internal string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Test seam: ends when every event raised so far has been sent.</summary>
    internal Task Sent => Locked(() => _sends);

    /// <summary>Test seam: ends when the install running now ends; already complete when none runs.</summary>
    internal Task WhenIdle => Locked(() => _running?.Task ?? Task.CompletedTask);

    /// <inheritdoc />
    public ModsRuntimeJob? Job => Locked(() => _job);

    private T Locked<T>(Func<T> read)
    {
        lock (_sync)
            return read();
    }

    /// <inheritdoc />
    public async Task<ModsRuntimeView> GetViewAsync(CancellationToken ct)
    {
        var release = releases.Current;
        var asset = release.AssetFor(BunRelease.CurrentRid());
        var configured = string.IsNullOrWhiteSpace(options.Harness.BunPath) ? null : options.Harness.BunPath;
        var location = await bun.FindAsync(release, ct).ConfigureAwait(false);

        ModsRuntimeBun? current = null;
        if (location is not null)
        {
            var safety = bun.SafetyOf(location, release);
            current = new ModsRuntimeBun(location.ExecutablePath, Display(location.ExecutablePath), location.Source, location.Version, safety.Safe, safety.Message);
        }

        return new ModsRuntimeView(
            current,
            configured,
            new ModsRuntimeRelease(
                release.Version,
                asset?.Size,
                asset is not null,
                Display(Path.Combine(Home, ".weave", "runtimes", "bun", release.Version)),
                SourceOf(options.Harness.BunDownloadBase)),
            Job);
    }

    /// <summary>The path with the home folder shortened to <c>~</c>, whichever separator follows it.</summary>
    internal string Display(string path)
    {
        if (string.IsNullOrEmpty(Home))
            return path;

        var rest = path.StartsWith(Home, StringComparison.Ordinal) ? path[Home.Length..] : null;
        return rest is not null && (rest.Length == 0 || rest[0] is '/' or '\\') ? "~" + rest : path;
    }

    private static string SourceOf(string? downloadBase)
    {
        var uri = BunRelease.ResolveDownloadBase(downloadBase, out _);
        return uri.Authority + uri.AbsolutePath.TrimEnd('/').Replace("/releases/download", "", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<bool> StartInstallAsync(string userId, CancellationToken ct)
    {
        var release = releases.Current;
        var found = await bun.FindAsync(release, ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (_running is not null)
                return true;

            if (!string.IsNullOrWhiteSpace(options.Harness.BunPath)
                || found is { Source: BunSources.Installed } && found.Version == release.Version)
                return false;

            var install = new Install(release, clock.GetUtcNow(), userId);
            _running = install;
            install.Task = Task.Run(() => RunAsync(install), CancellationToken.None);
            return true;
        }
    }

    private async Task RunAsync(Install install)
    {
        var succeeded = false;
        try
        {
            var result = await bun.EnsureAsync(install.Release, new SyncProgress(job => OnProgress(install, job)), install.Cancel.Token).ConfigureAwait(false);
            succeeded = result.IsSuccess;
            if (result.IsFailure)
                EnsureFailed(install, result.Error.Description);
        }
        catch (OperationCanceledException)
        {
            EnsureFailed(install, "The install was cancelled.", BunInstallFailures.Cancelled);
        }
        catch (Exception ex)
        {
            LogInstallCrashed(ex, install.Release.Version);
            EnsureFailed(install, $"Couldn't install Bun {install.Release.Version}: {ex.Message}");
        }

        try
        {
            if (!succeeded)
                await TurnOffAsync(install).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSwitchOffFailed(ex);
        }
        finally
        {
            Task sends;
            lock (_sync)
            {
                sends = _sends;
                _running = null;
            }

            await sends.ConfigureAwait(false);
            install.Cancel.Dispose();
        }
    }

    /// <summary>Makes sure the last job says failed, for a failure the installer ended without reporting.</summary>
    private void EnsureFailed(Install install, string message, string reason = BunInstallFailures.Other)
    {
        var current = Job;
        if (current is { Phase: BunInstallPhases.Failed } && current.StartedAt == install.StartedAt)
            return;

        OnProgress(install, new BunInstallJob(BunInstallPhases.Failed, install.Release.Version, message, current?.BytesReceived ?? 0, current?.BytesTotal, reason));
    }

    private void OnProgress(Install install, BunInstallJob reported)
    {
        var job = new ModsRuntimeJob
        {
            Phase = reported.Phase,
            Version = reported.Version,
            Message = reported.Message,
            Reason = reported.Phase == BunInstallPhases.Failed ? reported.Reason ?? BunInstallFailures.Other : null,
            BytesReceived = reported.BytesReceived,
            BytesTotal = reported.BytesTotal,
            StartedAt = install.StartedAt,
        };

        lock (_sync)
        {
            _job = job;

            var terminal = job.Phase is BunInstallPhases.Succeeded or BunInstallPhases.Failed;
            var phaseChanged = install.LastPhase != job.Phase;
            var now = clock.GetTimestamp();
            if (!terminal && !phaseChanged && clock.GetElapsedTime(install.LastRaised, now) < ProgressInterval)
                return;

            install.LastPhase = job.Phase;
            install.LastRaised = now;
            Send(new ModsRuntimePayload
            {
                Job = job,
                Reason = job.Phase == BunInstallPhases.Succeeded ? ModsRuntimeReasons.Installed : ModsRuntimeReasons.Job,
            });
        }
    }

    /// <summary>Queues an event behind the ones already queued, so they arrive in order. Call with <see cref="_sync"/> held.</summary>
    private void Send(ModsRuntimePayload payload)
    {
        _sends = _sends.ContinueWith(
            _ => BroadcastAsync(payload), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task BroadcastAsync(ModsRuntimePayload payload)
    {
        try
        {
            await events.BroadcastAsync(
                "sessions", EventTypes.ModsRuntime,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.ModsRuntimePayload),
                new ModsRuntimeChanged { Payload = payload }, userId: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBroadcastFailed(ex);
        }
    }

    /// <summary>A first install that ended without a Bun turns the switch back off for the user who asked for it.</summary>
    private async Task TurnOffAsync(Install install)
    {
        // A Bun that is usable after all means the switch can stay on.
        if (await bun.FindAsync(install.Release, CancellationToken.None).ConfigureAwait(false) is not null)
            return;

        using var scope = scopes.CreateScope();
        using (users.Begin(install.Requester))
        {
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>()
                .SetAsync(ModsFeature.PreferenceKey, "false").ConfigureAwait(false);
        }

        var changed = new ModsChangedPayload { Reason = "switch" };
        await events.BroadcastAsync(
            "sessions", EventTypes.ModsChanged,
            JsonSerializer.SerializeToElement(changed, ApplicationJsonContext.Default.ModsChangedPayload),
            new ModsChanged { Payload = changed }, install.Requester, CancellationToken.None).ConfigureAwait(false);
        LogSwitchedOff(install.Requester);
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(CancellationToken ct)
    {
        Install? install;
        lock (_sync)
            install = _running;
        if (install is null)
            return false;

        try
        {
            await install.Cancel.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return true; // It ended on its own as the cancel arrived.
        }

        await install.Task!.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private sealed class Install(BunRelease release, DateTimeOffset startedAt, string requester)
    {
        public BunRelease Release { get; } = release;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string Requester { get; } = requester;
        public CancellationTokenSource Cancel { get; } = new();
        public Task? Task { get; set; }
        public string? LastPhase { get; set; }
        public long LastRaised { get; set; }
    }

    /// <summary>Reports at once, on the caller's thread, so the order the installer reports in is the order seen.</summary>
    private sealed class SyncProgress(Action<BunInstallJob> report) : IProgress<BunInstallJob>
    {
        public void Report(BunInstallJob value) => report(value);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Installing Bun {Version} stopped unexpectedly.")]
    private partial void LogInstallCrashed(Exception ex, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turning Mods off after a failed install of the mod runtime failed.")]
    private partial void LogSwitchOffFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending a mods.runtime event failed.")]
    private partial void LogBroadcastFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Turned Mods off for {UserId}: the mod runtime couldn't be installed.")]
    private partial void LogSwitchedOff(string userId);
}
