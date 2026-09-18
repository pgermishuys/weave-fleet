using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Looks up each harness's latest version on npm (cached for an hour) and runs a harness's own updater when the
/// user asks. An update waits until no session is working, runs for at most five minutes, then checks the version
/// the harness reports. One update per harness at a time; its result stays until the user dismisses it.
/// </summary>
internal sealed partial class HarnessUpdateService(
    IHarnessRegistry registry,
    SessionActivityTracker activity,
    IHttpClientFactory httpClientFactory,
    ILogger<HarnessUpdateService> logger,
    TimeProvider? timeProvider = null) : IHarnessUpdateService
{
    internal static readonly TimeSpan LatestVersionTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedLookupTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);
    private const int OutputLines = 40;
    private const int MaxLineLength = 300;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string? Version, DateTimeOffset Expires)> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UpdateRun> _runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often a waiting update checks the working sessions again. Tests shorten it.</summary>
    internal TimeSpan WaitPoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Test seam: runs the updater. Defaults to starting the process.</summary>
    internal Func<HarnessCommand, TimeSpan, CancellationToken, Task<UpdaterResult>> RunUpdater { get; init; } = RunProcessAsync;

    /// <summary>Test seam: fetches a package's latest version. Defaults to the npm registry.</summary>
    internal Func<string, CancellationToken, Task<string?>>? FetchLatest { get; init; }

    internal sealed record UpdaterResult(int ExitCode, string Output, bool TimedOut);

    public async Task<IReadOnlyDictionary<string, HarnessUpdateInfo>> DescribeAsync(
        IReadOnlyList<HarnessInfo> harnesses,
        bool checkLatest,
        CancellationToken ct)
    {
        var runtimes = harnesses
            .Select(harness => (Harness: harness, Runtime: registry.GetRuntimeByType(harness.Type)))
            .Where(pair => pair.Runtime is not null)
            .ToList();

        var latest = checkLatest
            ? await LatestVersionsAsync(runtimes.Select(pair => pair.Runtime!.LatestVersionPackage), ct).ConfigureAwait(false)
            : new Dictionary<string, string?>();

        var result = new Dictionary<string, HarnessUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (harness, runtime) in runtimes)
        {
            var package = runtime!.LatestVersionPackage;
            var latestVersion = package is not null && latest.TryGetValue(package, out var found) ? found : null;
            var availability = AvailabilityOf(harness);
            var command = availability.ExecutablePath is null ? null : runtime.GetUpdateCommand(availability, latestVersion);
            var updateAvailable = command is not null
                && latestVersion is not null
                && harness.Version is not null
                && HarnessVersion.IsOlder(harness.Version, latestVersion);

            result[harness.Type] = new HarnessUpdateInfo
            {
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                MinimumVersion = runtime.MinimumVersion,
                Command = command?.Display,
                Job = _runs.TryGetValue(harness.Type, out var run) ? run.Job : null,
            };
        }
        return result;
    }

    public async Task<Result<HarnessUpdateJob>> StartAsync(string harnessType, CancellationToken ct)
    {
        var runtime = registry.GetRuntimeByType(harnessType);
        var harness = registry.GetByType(harnessType);
        if (runtime is null || harness is null)
            return FleetError.NotFoundFor("Harness", harnessType);

        var availability = await runtime.CheckAvailabilityAsync(ct).ConfigureAwait(false);
        if (availability.ExecutablePath is null)
            return FleetError.ValidationError("Harness", $"{harness.DisplayName} isn't installed, so there's nothing to update.");

        var package = runtime.LatestVersionPackage;
        var latest = package is null
            ? null
            : (await LatestVersionsAsync([package], ct).ConfigureAwait(false)).GetValueOrDefault(package);
        var command = runtime.GetUpdateCommand(availability, latest);
        if (command is null)
            return FleetError.ValidationError("Harness", $"Fleet can't update {harness.DisplayName}. Update it the way you installed it.");

        var run = new UpdateRun(new HarnessUpdateJob(HarnessUpdatePhases.Waiting, null, null, WorkingSessions(), availability.Version, null));
        if (!_runs.TryAdd(harness.Type, run))
        {
            if (!_runs.TryGetValue(harness.Type, out var existing) || !existing.IsFinished || !_runs.TryUpdate(harness.Type, run, existing))
                return new FleetError("Harness.Conflict", $"{harness.DisplayName} is already being updated.");
        }

        // The update outlives the request that started it.
        _ = Task.Run(() => RunAsync(harness.Type, harness.DisplayName, runtime, command, availability.Version, run), CancellationToken.None);
        return run.Job;
    }

    public Result<Unit> Dismiss(string harnessType)
    {
        if (!_runs.TryGetValue(harnessType, out var run))
            return Unit.Value;
        if (run.Job.Phase == HarnessUpdatePhases.Running)
            return new FleetError("Harness.Conflict", "The update is running and can't be stopped. It stops by itself after five minutes.");

        run.Cancel.Cancel();
        _runs.TryRemove(new KeyValuePair<string, UpdateRun>(harnessType, run));
        return Unit.Value;
    }

    private async Task RunAsync(
        string harnessType,
        string displayName,
        IHarnessRuntime runtime,
        HarnessCommand command,
        string? fromVersion,
        UpdateRun run)
    {
        try
        {
            // A running turn may be using the binary; wait for every session to finish.
            for (var working = WorkingSessions(); working > 0; working = WorkingSessions())
            {
                run.Job = run.Job with { WorkingSessions = working };
                await Task.Delay(WaitPoll, _time, run.Cancel.Token).ConfigureAwait(false);
            }

            run.Cancel.Token.ThrowIfCancellationRequested();
            run.Job = run.Job with { Phase = HarnessUpdatePhases.Running, WorkingSessions = 0 };
            LogUpdating(logger, harnessType, command.Display);
            var result = await RunUpdater(command, UpdateTimeout, CancellationToken.None).ConfigureAwait(false);

            var after = await runtime.CheckAvailabilityAsync(CancellationToken.None).ConfigureAwait(false);
            var toVersion = after.Version;
            if (result.TimedOut)
            {
                run.Job = Failed($"The update didn't finish within {UpdateTimeout.TotalMinutes:0} minutes. Run it yourself: {command.Display}");
            }
            else if (result.ExitCode != 0)
            {
                run.Job = Failed($"The update failed (exit code {result.ExitCode}). {displayName} is still on {fromVersion ?? "the old version"}. Run it yourself: {command.Display}");
            }
            else if (toVersion is not null && fromVersion is not null && !HarnessVersion.IsOlder(fromVersion, toVersion))
            {
                run.Job = Failed($"The update finished, but {displayName} is still on {toVersion}. Run it yourself: {command.Display}");
            }
            else
            {
                await runtime.AfterUpdateAsync(CancellationToken.None).ConfigureAwait(false);
                run.Job = run.Job with
                {
                    Phase = HarnessUpdatePhases.Succeeded,
                    Message = $"Updated {displayName} from {fromVersion} to {toVersion}.",
                    Output = result.Output,
                    ToVersion = toVersion,
                };
            }

            HarnessUpdateJob Failed(string message) => run.Job with
            {
                Phase = HarnessUpdatePhases.Failed,
                Message = message,
                Output = result.Output,
                ToVersion = toVersion,
            };
        }
        catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested)
        {
            // Dismissed while waiting; the run is already gone.
        }
        catch (Exception ex)
        {
            LogUpdateFailed(logger, ex, harnessType);
            run.Job = run.Job with
            {
                Phase = HarnessUpdatePhases.Failed,
                Message = $"Fleet couldn't run the update: {ex.Message} Run it yourself: {command.Display}",
            };
        }
    }

    private int WorkingSessions() =>
        activity.GetAll().Values.Count(snapshot => SessionActivityTracker.IsWorking(snapshot.ActivityStatus));

    private static HarnessAvailability AvailabilityOf(HarnessInfo harness) =>
        new(harness.Available, harness.Reason) { State = harness.State, Version = harness.Version, ExecutablePath = harness.ExecutablePath };

    /// <summary>The latest version of each package, from the cache or, when that's stale, npm.</summary>
    private async Task<Dictionary<string, string?>> LatestVersionsAsync(IEnumerable<string?> packages, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var wanted = packages.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var stale = wanted.Where(package => !_latest.TryGetValue(package, out var cached) || cached.Expires <= now).ToList();

        await Task.WhenAll(stale.Select(async package =>
        {
            var version = await LookUpAsync(package, ct).ConfigureAwait(false);
            _latest[package] = (version, now + (version is null ? FailedLookupTtl : LatestVersionTtl));
        })).ConfigureAwait(false);

        return wanted.ToDictionary(package => package, package => _latest.TryGetValue(package, out var cached) ? cached.Version : null, StringComparer.Ordinal);
    }

    private async Task<string?> LookUpAsync(string package, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            if (FetchLatest is not null)
                return await FetchLatest(package, timeout.Token).ConfigureAwait(false);

            using var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://registry.npmjs.org/{package}/latest", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogLookupFailed(logger, package, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Runs the updater without a terminal: stdin is closed so an updater that asks a question ends instead of
    /// waiting, and output is kept to its last lines. Killed after <paramref name="timeout"/>.
    /// </summary>
    private static async Task<UpdaterResult> RunProcessAsync(HarnessCommand command, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments) psi.ArgumentList.Add(argument);
        TerminalEnvironment.RemoveFleetOwned(psi.Environment);

        var lines = new Queue<string>();
        void Keep(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // A spinner redraws its line with \r or cursor codes; keep what's left at the end.
            var text = Ansi().Replace(line, string.Empty);
            text = text[(text.LastIndexOf('\r') + 1)..].TrimEnd();
            if (text.Length > MaxLineLength) text = "…" + text[^MaxLineLength..];
            if (text.Length == 0) return;
            lock (lines)
            {
                lines.Enqueue(text);
                while (lines.Count > OutputLines) lines.Dequeue();
            }
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new UpdaterResult(-1, $"Couldn't start {command.Executable}: {ex.Message}", TimedOut: false);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
        }

        string output;
        lock (lines) output = string.Join('\n', lines);
        return new UpdaterResult(timedOut ? -1 : process.ExitCode, output, timedOut);
    }

    /// <summary>One harness's update: its current job and the switch that cancels it while waiting.</summary>
    private sealed class UpdateRun(HarnessUpdateJob job)
    {
        private HarnessUpdateJob _job = job;

        public HarnessUpdateJob Job
        {
            get => Volatile.Read(ref _job);
            set => Volatile.Write(ref _job, value);
        }

        public CancellationTokenSource Cancel { get; } = new();

        public bool IsFinished => Job.Phase is HarnessUpdatePhases.Succeeded or HarnessUpdatePhases.Failed;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating harness {HarnessType}: {Command}")]
    private static partial void LogUpdating(ILogger logger, string harnessType, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updating harness {HarnessType} failed")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception, string harnessType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't look up the latest version of {Package}: {Reason}")]
    private static partial void LogLookupFailed(ILogger logger, string package, string reason);
}
