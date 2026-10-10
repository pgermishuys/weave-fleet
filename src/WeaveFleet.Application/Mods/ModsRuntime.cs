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
/// Installs the Bun mods run on and tells clients how it's going. One install runs at a time, in the background, apart
/// from the request that started it. A user turning Mods on starts one; so do Fleet's start-up, a new release and a
/// periodic check, when someone's mods need it. A first install that fails or is cancelled turns the switch back off for
/// the user who started it; an install Fleet started itself never touches a switch, since mods keep running on the Bun
/// they have.
/// </summary>
public sealed partial class ModsRuntime(
    IBunRuntime bun,
    IBunReleases releases,
    IBunPathSetting bunPath,
    IModsPreferenceReader preferenceReader,
    ModsSafeMode safeMode,
    IEventBroadcaster events,
    IServiceScopeFactory scopes,
    IBackgroundUserScope users,
    FleetOptions options,
    TimeProvider clock,
    ILogger<ModsRuntime> logger) : IModsRuntime
{
    /// <summary>The user a switch set in configuration (<c>Fleet:Harness:Mods</c>) is taken to be for.</summary>
    private const string LocalUserId = "local-user";

    private readonly object _sync = new();
    private Install? _running;
    private ModsRuntimeJob? _job;
    private Task _sends = Task.CompletedTask;

    /// <summary>Test seam: the user's home folder, shortened to <c>~</c> in paths shown to the user.</summary>
    internal string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Test seam: the platform to describe the release for.</summary>
    internal string Rid { get; init; } = BunRelease.CurrentRid();

    /// <summary>Test seam: the least time between two download progress events.</summary>
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Test seam: how long a user turning Mods on waits for Fleet's manifest before installing the release it has.</summary>
    internal TimeSpan RefreshCap { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Test seam: ends when every event raised so far has been sent.</summary>
    internal Task Sent
    {
        get
        {
            lock (_sync)
                return _sends;
        }
    }

    /// <summary>Test seam: ends when the install running now ends; already complete when none runs.</summary>
    internal Task WhenIdle
    {
        get
        {
            lock (_sync)
                return _running?.Task ?? Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public event EventHandler<BunChangedEventArgs>? BunChanged;

    /// <inheritdoc />
    public ModsRuntimeJob? Job
    {
        get
        {
            lock (_sync)
                return _job;
        }
    }

    // ── What the user sees ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ModsRuntimeView> GetViewAsync(string userId, CancellationToken ct)
    {
        var release = releases.Current;
        var asset = release.AssetFor(Rid);
        var path = await bunPath.GetAsync(userId, ct).ConfigureAwait(false);
        var location = await bun.FindForUserAsync(release, userId, ct).ConfigureAwait(false);

        ModsRuntimeBun? current = null;
        if (location is not null)
        {
            var safety = bun.SafetyOf(location, release);
            current = new ModsRuntimeBun(location.ExecutablePath, Display(location.ExecutablePath), location.Source, location.Version, safety.Safe, safety.Message);
        }

        string? error = null;
        if (path is not null && location is null)
            error = await WhyItCannotRunAsync(path, release, ct).ConfigureAwait(false);

        var installed = bun.Installed();
        var own = installed.Count > 0 ? installed[0] : null;
        ModsRuntimeUpdate? update = null;
        if (own is not null && bun.SafetyOf(own, release) is { UpdateAvailable: true } ownSafety)
            update = new ModsRuntimeUpdate(release.Version, Security: !ownSafety.Safe);

        return new ModsRuntimeView(
            current,
            path,
            error,
            bunPath.FromConfiguration is not null,
            new ModsRuntimeRelease(
                release.Version,
                release.OldestSafe,
                release.Note,
                asset?.Size,
                asset is not null,
                Display(Path.Combine(Home, ".weave", "runtimes", "bun", release.Version)),
                SourceOf(options.Harness.BunDownloadBase)),
            InstalledSize(installed),
            Job,
            update);
    }

    private async Task<string> WhyItCannotRunAsync(string path, BunRelease release, CancellationToken ct)
    {
        // The machine owner's path has an answer of its own; a user's is judged the way the Settings box judges what they type.
        if (bunPath.FromConfiguration is not null)
        {
            var ensured = await bun.EnsureAsync(release, progress: null, ct).ConfigureAwait(false);
            return ensured.IsFailure ? ensured.Error.Description : "Fleet couldn't run it.";
        }

        var candidate = await bun.CheckAsync(path, ct).ConfigureAwait(false);
        return candidate.Message ?? "Fleet couldn't run it.";
    }

    private static long InstalledSize(IReadOnlyList<BunLocation> installed)
    {
        long total = 0;
        foreach (var location in installed)
        {
            try
            {
                var folder = Path.GetDirectoryName(location.ExecutablePath);
                if (folder is null || !Directory.Exists(folder))
                    continue;

                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                    total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that can't be read counts as nothing.
            }
        }

        return total;
    }

    /// <summary>The path with the home folder shortened to <c>~</c>, whichever separator follows it.</summary>
    internal string Display(string path)
    {
        if (string.IsNullOrEmpty(Home))
            return path;

        var home = Home.TrimEnd('/', '\\');
        if (home.Length == 0 || !path.StartsWith(home, StringComparison.Ordinal))
            return path;

        var rest = path[home.Length..];
        return rest.Length == 0 || rest[0] is '/' or '\\' ? "~" + rest : path;
    }

    private static string SourceOf(string? downloadBase)
    {
        var uri = BunRelease.ResolveDownloadBase(downloadBase, out _);
        var path = uri.AbsolutePath.TrimEnd('/');
        const string GitHubSuffix = "/releases/download";
        if (path.EndsWith(GitHubSuffix, StringComparison.Ordinal))
            path = path[..^GitHubSuffix.Length];
        return uri.Authority + path;
    }

    // ── Installing ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> StartInstallAsync(string? kind, string? requestedByUserId, CancellationToken ct)
    {
        if (Join(requestedByUserId))
            return true;

        // A user turning Mods on gets the latest recommended release when Fleet's manifest answers in time.
        if (requestedByUserId is not null)
            await RefreshAsync(ct).ConfigureAwait(false);

        var release = releases.Current;
        var found = await bun.FindAsync(release, ct).ConfigureAwait(false);
        if (found is { Source: BunSources.Installed } && found.Version == release.Version)
            return false;
        if (bunPath.FromConfiguration is not null)
            return false; // The machine owner's Bun runs mods; Fleet installs nothing.

        kind ??= found is null ? ModsRuntimeKinds.Install : bun.SafetyOf(found, release).Safe ? ModsRuntimeKinds.Update : ModsRuntimeKinds.Security;

        lock (_sync)
        {
            if (_running is not null)
            {
                if (requestedByUserId is not null)
                    _running.Requesters.Add(requestedByUserId);
                return true;
            }

            var install = new Install(kind, release, clock.GetUtcNow(), found?.Version);
            if (requestedByUserId is not null)
                install.Requesters.Add(requestedByUserId);
            _running = install;
            install.Task = Task.Run(() => RunAsync(install), CancellationToken.None);
            return true;
        }
    }

    private bool Join(string? requestedByUserId)
    {
        lock (_sync)
        {
            if (_running is null)
                return false;
            if (requestedByUserId is not null)
                _running.Requesters.Add(requestedByUserId);
            return true;
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var cap = new CancellationTokenSource(RefreshCap, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cap.Token);
            await releases.RefreshAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogRefreshTimedOut();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRefreshFailed(ex);
        }
    }

    private async Task RunAsync(Install install)
    {
        var progress = new SyncProgress(job => OnProgress(install, job));
        var succeeded = false;
        try
        {
            var result = await bun.EnsureAsync(install.Release, progress, install.Cancel.Token).ConfigureAwait(false);
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
            if (succeeded)
            {
                RaiseBunChanged(userId: null);
            }
            else if (install.Kind == ModsRuntimeKinds.Install)
            {
                await TurnOffForRequestersAsync(install).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogFollowUpFailed(ex);
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
        ModsRuntimeJob? current;
        lock (_sync)
            current = _job;

        if (current is { Phase: BunInstallPhases.Failed } && current.StartedAt == install.StartedAt)
            return;

        OnProgress(install, new BunInstallJob(BunInstallPhases.Failed, install.Release.Version, message, current?.BytesReceived ?? 0, current?.BytesTotal, reason));
    }

    private void OnProgress(Install install, BunInstallJob reported)
    {
        var job = new ModsRuntimeJob
        {
            Phase = reported.Phase,
            Kind = install.Kind,
            Version = reported.Version,
            Message = reported.Message,
            Reason = reported.Phase == BunInstallPhases.Failed ? reported.Reason ?? BunInstallFailures.Other : null,
            BytesReceived = reported.BytesReceived,
            BytesTotal = reported.BytesTotal,
            StartedAt = install.StartedAt,
            From = install.From,
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
            }, userId: null);
        }
    }

    /// <summary>Queues an event behind the ones already queued, so they arrive in order. Call with <see cref="_sync"/> held.</summary>
    private void Send(ModsRuntimePayload payload, string? userId)
    {
        _sends = _sends.ContinueWith(
            _ => BroadcastAsync(payload, userId), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task BroadcastAsync(ModsRuntimePayload payload, string? userId)
    {
        try
        {
            await events.BroadcastAsync(
                "sessions",
                EventTypes.ModsRuntime,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.ModsRuntimePayload),
                new ModsRuntimeChanged { Payload = payload },
                userId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBroadcastFailed(ex);
        }
    }

    /// <summary>A first install that ended without a Bun turns the switch back off for the users who asked for it.</summary>
    private async Task TurnOffForRequestersAsync(Install install)
    {
        string[] requesters;
        lock (_sync)
            requesters = [.. install.Requesters];

        foreach (var userId in requesters)
        {
            // A Bun that turned up meanwhile (another install, a path saved) means the switch can stay on.
            if (await bun.FindForUserAsync(install.Release, userId, CancellationToken.None).ConfigureAwait(false) is not null)
                continue;

            using var scope = scopes.CreateScope();
            using (users.Begin(userId))
            {
                await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>()
                    .SetAsync(ModsFeature.PreferenceKey, "false").ConfigureAwait(false);
            }

            var changed = new ModsChangedPayload { Reason = "switch" };
            await events.BroadcastAsync(
                "sessions",
                EventTypes.ModsChanged,
                JsonSerializer.SerializeToElement(changed, ApplicationJsonContext.Default.ModsChangedPayload),
                new ModsChanged { Payload = changed },
                userId,
                CancellationToken.None).ConfigureAwait(false);
            LogSwitchedOff(userId);
        }
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

    // ── The user's own Bun ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModsRuntimeCandidate>> FindOnMachineAsync(CancellationToken ct)
    {
        var found = await bun.FindOnMachineAsync(ct).ConfigureAwait(false);
        return [.. found.Select(c => new ModsRuntimeCandidate(c.Path, Display(c.Path), c.ResolvedPath, c.Version, c.Status, c.Message))];
    }

    /// <inheritdoc />
    public async Task SaveBunPathAsync(string userId, string? path, CancellationToken ct)
    {
        await bunPath.SaveAsync(userId, path, ct).ConfigureAwait(false);
        lock (_sync)
            Send(new ModsRuntimePayload { Job = _job, Reason = ModsRuntimeReasons.BunPath }, userId);

        RaiseBunChanged(userId);
    }

    private void RaiseBunChanged(string? userId)
    {
        try
        {
            BunChanged?.Invoke(this, new BunChangedEventArgs(userId));
        }
        catch (Exception ex)
        {
            LogBunChangedHandlerFailed(ex);
        }
    }

    // ── What Fleet installs by itself ───────────────────────────────────

    /// <inheritdoc />
    public async Task ReleaseChangedAsync(CancellationToken ct)
    {
        lock (_sync)
            Send(new ModsRuntimePayload { Job = _job, Reason = ModsRuntimeReasons.Release }, userId: null);

        await EvaluateAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(CancellationToken ct)
    {
        if (!await IsWantedAsync(ct).ConfigureAwait(false))
            return;

        var release = releases.Current;
        var found = await bun.FindAsync(release, ct).ConfigureAwait(false);
        if (found is { Source: BunSources.Installed } && found.Version == release.Version)
            return;

        await StartInstallAsync(kind: null, requestedByUserId: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether someone's mods need Fleet's own Bun: a user has Mods on (stored, or the option for the local user who has
    /// no stored choice) and is not in safe mode and has no Bun of their own, and the machine owner set none either.
    /// </summary>
    private async Task<bool> IsWantedAsync(CancellationToken ct)
    {
        if (bunPath.FromConfiguration is not null)
            return false;

        var rows = await preferenceReader.ListAsync(ct).ConfigureAwait(false);
        var on = new HashSet<string>(StringComparer.Ordinal);
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Key == ModsFeature.PreferenceKey && !string.IsNullOrWhiteSpace(row.Value))
            {
                chosen.Add(row.UserId);
                if (string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase))
                    on.Add(row.UserId);
            }
            else if (row.Key == BunPathPreference.Key && !string.IsNullOrWhiteSpace(row.Value))
            {
                own.Add(row.UserId);
            }
        }

        if (options.Harness.Mods && !chosen.Contains(LocalUserId))
            on.Add(LocalUserId);

        return on.Any(user => !safeMode.IsOn(user) && !own.Contains(user));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed class Install(string kind, BunRelease release, DateTimeOffset startedAt, string? from)
    {
        public string Kind { get; } = kind;
        public BunRelease Release { get; } = release;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string? From { get; } = from;
        public HashSet<string> Requesters { get; } = new(StringComparer.Ordinal);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fleet's Bun manifest didn't answer in time; installing the release Fleet has.")]
    private partial void LogRefreshTimedOut();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refreshing Fleet's Bun manifest failed; installing the release Fleet has.")]
    private partial void LogRefreshFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Installing Bun {Version} stopped unexpectedly.")]
    private partial void LogInstallCrashed(Exception ex, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Following up an install of the mod runtime failed.")]
    private partial void LogFollowUpFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending a mods.runtime event failed.")]
    private partial void LogBroadcastFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A listener to the Bun change failed.")]
    private partial void LogBunChangedHandlerFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Turned Mods off for {UserId}: the mod runtime couldn't be installed.")]
    private partial void LogSwitchedOff(string userId);
}
