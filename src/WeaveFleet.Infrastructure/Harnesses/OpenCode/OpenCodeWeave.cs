using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Weave;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Hands a user's Fleet-kept Weave config to pooled OpenCode. New processes get the config folder through
/// <see cref="WeaveEnvironment"/>. After a save, each folder a running process has loaded is reloaded with
/// <c>POST /instance/dispose</c> as soon as no session in it is busy, since a dispose cancels running turns.
/// </summary>
internal sealed partial class OpenCodeWeave(
    PooledOpenCodeInstanceRegistry registry,
    Func<string> dataDirectory,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    internal const string WeavePackage = "@weaveio/weave-adapter-opencode";
    internal const string LegacyPackage = "@opencode_weave/weave";
    internal const string WeaveProbeAgent = "fleet-weave-probe";
    internal const string LegacyProbeAgent = "fleet-legacy-probe";

    /// <summary>Weave marks the agents it adds with this at the end of their description.</summary>
    internal const string WeaveManagedMarker = "[weave-managed]";

    internal static readonly TimeSpan ApplyPollInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ApplyGiveUpAfter = TimeSpan.FromMinutes(30);

    /// <summary>A probe config for each Weave: if the probe agent shows up, that Weave reads the folder Fleet points it at.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProbeFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [WeaveConfigFiles.Config] = $$"""
            agent {{WeaveProbeAgent}} {
              description "Fleet checks that Weave reads its config folder"
              prompt "Fleet probe"
              mode subagent
            }
            """,
        [WeaveConfigFiles.LegacyConfig] = $$"""
            {
              "custom_agents": {
                "{{LegacyProbeAgent}}": {
                  "description": "Fleet checks that Weave Legacy reads its config folder",
                  "prompt": "Fleet probe",
                  "mode": "subagent"
                }
              }
            }
            """,
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _trialLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ApplyRun> _applies = new(StringComparer.Ordinal);

    /// <summary>The variables that point Weave at the user's folder, when they keep their config in Fleet.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetEnvironmentAsync(string userId)
    {
        var config = await LoadConfigAsync(userId).ConfigureAwait(false);
        if (config is not { Source: WeaveConfigSource.Fleet } || config.Files.Count == 0)
            return new Dictionary<string, string>();

        var folder = WeaveConfigFolder.ForUser(dataDirectory(), userId);
        WeaveConfigFolder.Mirror(folder, config.Files);
        return EnvironmentFor(folder, config.Files);
    }

    internal static Dictionary<string, string> EnvironmentFor(string folder, IReadOnlyDictionary<string, string> files)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var flavor in new[] { WeaveFlavor.Weave, WeaveFlavor.Legacy })
        {
            if (files.ContainsKey(WeaveConfigFiles.ConfigFor(flavor)))
                environment[WeaveEnvironment.For(flavor)] = folder;
        }

        return environment;
    }

    /// <summary>
    /// Which Weave plugins OpenCode loads for the user, from its <c>/config</c>, and whether each reads the folder Fleet
    /// points it at, from whether its probe agent shows up.
    /// </summary>
    public async Task<IReadOnlyList<WeaveInstall>> DetectAsync(string ownerUserId, CancellationToken ct)
    {
        var (plugins, agents, _) = await TryAsync(ownerUserId, ProbeFiles, readPlugins: true, ct).ConfigureAwait(false);
        var names = agents.Select(agent => agent.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var installs = new List<WeaveInstall>();
        foreach (var entry in plugins)
        {
            var package = PackageName(entry);
            var flavor = package switch
            {
                WeavePackage => WeaveFlavor.Weave,
                LegacyPackage => WeaveFlavor.Legacy,
                _ => (WeaveFlavor?)null,
            };
            if (flavor is null)
                continue;

            var probe = flavor == WeaveFlavor.Weave ? WeaveProbeAgent : LegacyProbeAgent;
            installs.Add(new WeaveInstall(flavor.Value, package!, entry, names.Contains(probe)));
        }

        return installs;
    }

    /// <summary>Loads a draft the way a session would and lists the <paramref name="flavor"/> agents OpenCode got.</summary>
    public async Task<WeaveCheck> CheckAsync(
        string ownerUserId,
        WeaveFlavor flavor,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
    {
        var (_, agents, logStart) = await TryAsync(ownerUserId, files, readPlugins: false, ct).ConfigureAwait(false);
        var loaded = agents
            .Where(agent => agent.Name is not null && IsFlavorAgent(agent, flavor))
            .Select(agent => agent.Name!)
            .ToList();
        if (loaded.Count > 0)
            return new WeaveCheck(true, loaded);

        if (flavor == WeaveFlavor.Legacy)
            return new WeaveCheck(false, [], "OpenCode started, but Weave Legacy added no agents.");

        return new WeaveCheck(
            false,
            [],
            "OpenCode started, but Weave added no agents. Weave ignores the whole file when it can't read it.",
            ReadWeaveLogErrors(TrialLogFor(ownerUserId), logStart));
    }

    /// <summary>
    /// The user saved: writes the folder, then reloads every folder their running processes loaded with it. Busy folders
    /// are retried until they're idle.
    /// </summary>
    public async Task ConfigChangedAsync(string ownerUserId, CancellationToken ct)
    {
        var config = await LoadConfigAsync(ownerUserId).ConfigureAwait(false);
        var folder = WeaveConfigFolder.ForUser(dataDirectory(), ownerUserId);
        if (config is { Source: WeaveConfigSource.Fleet } && config.Files.Count > 0)
            WeaveConfigFolder.Mirror(folder, config.Files);

        var targets = new List<ApplyTarget>();
        foreach (var (instance, environment) in registry.GetRunningInstances(ownerUserId))
        {
            var reads = environment.TryGetValue(WeaveEnvironment.GlobalConfigDir, out var global) && global == folder
                        || environment.TryGetValue(WeaveEnvironment.LegacyConfigDir, out var legacy) && legacy == folder;
            if (!reads)
                continue;

            // Warm-up leases the process for Fleet's own working folder without loading it; reloading it would load it.
            foreach (var directory in instance.Directories.Where(d => d != Environment.CurrentDirectory))
                targets.Add(new ApplyTarget(instance, directory));
        }

        var run = new ApplyRun(targets);
        if (_applies.TryGetValue(ownerUserId, out var previous))
            await previous.Cancellation.CancelAsync().ConfigureAwait(false);
        _applies[ownerUserId] = run;

        await run.PassAsync(logger, ct).ConfigureAwait(false);
        if (!run.Done)
            _ = Task.Run(() => run.RetryUntilDoneAsync(logger), CancellationToken.None);
    }

    public WeaveApplyStatus? GetApplyStatus(string ownerUserId) =>
        _applies.TryGetValue(ownerUserId, out var run) ? run.Status() : null;

    /// <summary>
    /// Writes <paramref name="files"/> into the user's trial folder, reloads the trial project so Weave reads them, and
    /// returns OpenCode's plugins (when asked) and agents there, and where this trial's lines start in Weave's log. One
    /// trial at a time per user: they share the folder.
    /// </summary>
    private async Task<(IReadOnlyList<string> Plugins, IReadOnlyList<OpenCodeAgentInfo> Agents, long LogStart)> TryAsync(
        string ownerUserId,
        IReadOnlyDictionary<string, string> files,
        bool readPlugins,
        CancellationToken ct)
    {
        var gate = _trialLocks.GetOrAdd(ownerUserId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = WeaveConfigFolder.TrialFor(dataDirectory(), ownerUserId);
            var project = WeaveConfigFolder.TrialProjectFor(dataDirectory(), ownerUserId);
            WeaveConfigFolder.Mirror(folder, files);
            Directory.CreateDirectory(project);

            // The trial process keeps Weave's log open between tries, and Windows won't delete an open file, so read
            // only what this try adds.
            var log = new FileInfo(TrialLogFor(ownerUserId));
            var logStart = log.Exists ? log.Length : 0;

            // Both variables point at the trial folder, so this is one process per user however often they try.
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WeaveEnvironment.GlobalConfigDir] = folder,
                [WeaveEnvironment.LegacyConfigDir] = folder,
            };
            var lease = await registry
                .AcquireAsync(ownerUserId, CredentialHasher.HashEnvironment(environment), environment, project, ct)
                .ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                var client = lease.Instance.HttpClient
                    ?? throw new InvalidOperationException("Pooled OpenCode instance does not expose an HTTP client.");

                // The project may already be loaded with the last trial's files.
                await client.DisposeInstanceAsync(project, ct).ConfigureAwait(false);
                var plugins = readPlugins
                    ? await client.GetConfigPluginsAsync(project, ct).ConfigureAwait(false)
                    : [];
                var agents = await client.GetAgentsAsync(project, ct).ConfigureAwait(false);
                return (plugins, agents, logStart);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string TrialLogFor(string ownerUserId) =>
        Path.Combine(WeaveConfigFolder.TrialProjectFor(dataDirectory(), ownerUserId), ".weave", "weave.log");

    private async Task<WeaveConfig?> LoadConfigAsync(string userId)
    {
        using var userScope = BackgroundUserContext.BeginScope(userId);
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<IWeaveConfigRepository>() is { } configs
            ? await configs.GetAsync().ConfigureAwait(false)
            : null;
    }

    internal static bool IsFlavorAgent(OpenCodeAgentInfo agent, WeaveFlavor flavor)
    {
        var weaveManaged = agent.Description?.EndsWith(WeaveManagedMarker, StringComparison.Ordinal) == true;
        return flavor == WeaveFlavor.Weave ? weaveManaged : agent.Native == false && !weaveManaged;
    }

    /// <summary>
    /// The npm package a plugin entry comes from: <c>@scope/name@1.2.3</c> and <c>name@next</c> lose the version, and a
    /// <c>file://</c> path is looked up in the nearest <c>package.json</c> above it.
    /// </summary>
    internal static string? PackageName(string entry)
    {
        if (entry.StartsWith("file://", StringComparison.Ordinal))
            return Uri.TryCreate(entry, UriKind.Absolute, out var uri) ? PackageNameAt(uri.LocalPath) : null;

        var versionAt = entry.LastIndexOf('@');
        return versionAt > 0 ? entry[..versionAt] : entry;
    }

    private static string? PackageNameAt(string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var manifest = Path.Combine(directory, "package.json");
            if (!File.Exists(manifest))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The errors in Weave's last "Failed to load Weave config" line after <paramref name="from"/>, one per problem:
    /// <c>config.weave:3:1 UnclosedBlock</c>.
    /// </summary>
    internal static IReadOnlyList<string> ReadWeaveLogErrors(string logPath, long from = 0)
    {
        if (!File.Exists(logPath))
            return [];

        string? line;
        try
        {
            // Weave still has the file open for writing, which Windows only lets another reader share with ReadWrite.
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(from <= stream.Length ? from : 0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            line = ReadLines(reader).LastOrDefault(l => l.Contains("\"ParseError\"", StringComparison.Ordinal)
                                                        || l.Contains("Failed to load Weave config", StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return [];
        }

        if (line is null)
            return [];

        var details = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return document.RootElement.TryGetProperty("msg", out var msg) && msg.GetString() is { } text ? [text] : [];

            foreach (var error in errors.EnumerateArray())
            {
                var file = error.TryGetProperty("path", out var path) && path.GetString() is { } p ? Path.GetFileName(p) : WeaveConfigFiles.Config;
                if (error.TryGetProperty("errors", out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    foreach (var problem in inner.EnumerateArray())
                    {
                        var type = problem.TryGetProperty("type", out var t) ? t.GetString() : "Error";
                        var at = problem.TryGetProperty("line", out var l) && l.TryGetInt32(out var lineNumber)
                            ? $":{lineNumber}" + (problem.TryGetProperty("column", out var c) && c.TryGetInt32(out var column) ? $":{column}" : string.Empty)
                            : string.Empty;
                        var message = problem.TryGetProperty("message", out var m) && m.GetString() is { } said ? $" {said}" : string.Empty;
                        details.Add($"{file}{at} {type}{message}");
                    }
                }
                else
                {
                    var type = error.TryGetProperty("type", out var t) ? t.GetString() : "Error";
                    var message = error.TryGetProperty("message", out var m) && m.GetString() is { } said ? $" {said}" : string.Empty;
                    details.Add($"{file} {type}{message}");
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return details;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private sealed record ApplyTarget(PooledOpenCodeInstance Instance, string Directory);

    /// <summary>One save's reload: which folders are done, and the loop that waits for the busy ones.</summary>
    private sealed partial class ApplyRun(IReadOnlyList<ApplyTarget> targets)
    {
        private readonly ConcurrentDictionary<ApplyTarget, bool> _reloaded = new(targets.Select(t => KeyValuePair.Create(t, false)));
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private string? _error;

        public CancellationTokenSource Cancellation { get; } = new();

        public bool Done => _reloaded.Values.All(reloaded => reloaded);

        public WeaveApplyStatus Status()
        {
            // One row per folder: a folder two processes loaded is done when both have reloaded it.
            var folders = _reloaded
                .GroupBy(pair => pair.Key.Directory, StringComparer.Ordinal)
                .Select(group => new WeaveApplyFolder(group.Key, group.All(pair => pair.Value)))
                .OrderBy(folder => folder.Directory, StringComparer.Ordinal)
                .ToList();
            return new WeaveApplyStatus(folders, _error);
        }

        public async Task PassAsync(ILogger logger, CancellationToken ct)
        {
            foreach (var (target, reloaded) in _reloaded)
            {
                if (reloaded || Cancellation.IsCancellationRequested)
                    continue;

                // A process that stopped has nothing to reload: its next start reads the folder.
                if (!target.Instance.IsAvailable || target.Instance.HttpClient is not { } client)
                {
                    _reloaded[target] = true;
                    continue;
                }

                try
                {
                    var statuses = await client.GetSessionStatusAsync(target.Directory, ct).ConfigureAwait(false);
                    if (statuses.Values.Any(status => status is not OpenCodeIdleStatus))
                        continue;

                    await client.DisposeInstanceAsync(target.Directory, ct).ConfigureAwait(false);
                    _reloaded[target] = true;
                    LogFolderReloaded(logger, target.Instance.InstanceId, target.Directory);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    _error = $"Couldn't reload {target.Directory}: {ex.Message}";
                    LogFolderReloadFailed(logger, ex, target.Instance.InstanceId, target.Directory);
                }
            }
        }

        public async Task RetryUntilDoneAsync(ILogger logger)
        {
            var ct = Cancellation.Token;
            try
            {
                while (!Done && !ct.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow - _started > ApplyGiveUpAfter)
                    {
                        _error = "Some folders stayed busy for 30 minutes. They'll use the saved config after their process restarts.";
                        return;
                    }

                    await Task.Delay(ApplyPollInterval, ct).ConfigureAwait(false);
                    await PassAsync(logger, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A newer save took over.
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Reloaded {Directory} on OpenCode {InstanceId} for a Weave config change")]
        private static partial void LogFolderReloaded(ILogger logger, string instanceId, string directory);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reload {Directory} on OpenCode {InstanceId} for a Weave config change")]
        private static partial void LogFolderReloadFailed(ILogger logger, Exception ex, string instanceId, string directory);
    }
}
