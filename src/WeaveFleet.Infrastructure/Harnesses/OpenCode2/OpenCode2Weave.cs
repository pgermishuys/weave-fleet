using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Weave;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Weave in OpenCode 2. Weave's V2 adapter (<see cref="Package"/>) is a plugin in the user's config (<c>plugins</c>).
/// When the user keeps their Weave config in Fleet, servers start with <see cref="WeaveEnvironment.GlobalConfigDir"/>
/// pointing at the folder Fleet writes (<see cref="OpenCode2ServerSetup.WeaveConfigFolder"/>).
/// <para>
/// What V2 loads is read from a throwaway server with its log on (<see cref="TryAsync"/>): <c>GET /api/plugin</c> lists
/// only the plugins V2 loaded, and why one didn't load is only in its log. A change reaches running servers with
/// <c>POST /api/location/reload</c>, which reads the config again, plugins included, and lets running turns go on;
/// it cancels open questions, so Fleet reloads a server once none of its sessions waits on one.
/// </para>
/// </summary>
internal sealed partial class OpenCode2Weave(
    Func<string> dataDirectory,
    IServiceScopeFactory scopeFactory,
    Func<string, string, Action<string>, CancellationToken, Task<OpenCode2Server>> startTrial,
    Func<string, IReadOnlyList<OpenCode2Server>> runningServers,
    ILogger logger)
{
    internal const string Package = "@weaveio/weave-adapter-opencode2";
    internal const string ProbeAgent = "fleet-weave-probe";

    /// <summary>Weave's V2 adapter starts the description of each agent it adds with this.</summary>
    internal const string WeaveManagedMarker = "[weave-managed]";

    internal static readonly TimeSpan ReloadPollInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ReloadGiveUpAfter = TimeSpan.FromMinutes(30);

    /// <summary>How long a try waits for Weave's agents once V2 has loaded the folder: Weave adds them after it reads its config.</summary>
    internal TimeSpan AgentWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a try waits after the folder loaded for the rest of V2's log.</summary>
    internal TimeSpan LogGrace { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a try waits for a Weave the config lists to load or fail: V2 installs a package from npm the first
    /// time, which takes seconds, and loads the folder without waiting for it.
    /// </summary>
    internal TimeSpan PluginWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A config whose agent shows up only when Weave reads the folder Fleet points it at.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProbeFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [WeaveConfigFiles.Config] = $$"""
            agent {{ProbeAgent}} {
              description "Fleet checks that Weave reads its config folder"
              prompt "Fleet probe"
              mode subagent
            }
            """,
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _trialLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReloadRun> _reloads = new(StringComparer.Ordinal);

    /// <summary>
    /// The folder Weave reads when the owner keeps their Weave config in Fleet, written with the saved files; null when
    /// Weave reads the user's own.
    /// </summary>
    public async Task<string?> GetConfigFolderAsync(string ownerUserId)
    {
        var config = await LoadConfigAsync(ownerUserId).ConfigureAwait(false);
        if (config is not { Source: WeaveConfigSource.Fleet } || !config.Files.ContainsKey(WeaveConfigFiles.Config))
            return null;

        var folder = WeaveConfigFolder.ForUser(dataDirectory(), ownerUserId);
        WeaveConfigFolder.Mirror(folder, config.Files);
        return folder;
    }

    /// <summary>
    /// Which Weave V2 loads for the owner: the adapter in its plugin list, whether it reads the folder Fleet points it at
    /// (its probe agent shows up), and why it didn't load when it's listed but V2 couldn't load it.
    /// </summary>
    public async Task<IReadOnlyList<WeaveInstall>> DetectAsync(string ownerUserId, CancellationToken ct)
    {
        var trial = await TryAsync(ownerUserId, ProbeFiles, ct).ConfigureAwait(false);
        var probed = trial.Agents.Any(agent => agent.Id == ProbeAgent);

        var installs = new List<WeaveInstall>();
        foreach (var plugin in trial.Plugins)
        {
            if (Describe(plugin) is { Package: Package } found)
                installs.Add(new WeaveInstall(WeaveFlavor.Weave, Package, found.Entry, probed));
        }

        foreach (var (target, cause) in FailedPlugins(trial.Log))
        {
            if (PackageOf(target) == Package && installs.All(install => install.Entry != target))
                installs.Add(new WeaveInstall(WeaveFlavor.Weave, Package, target, false, cause));
        }

        return installs;
    }

    /// <summary>Loads a draft the way a session would, and lists the Weave agents V2 got.</summary>
    public async Task<WeaveCheck?> CheckAsync(
        string ownerUserId,
        WeaveFlavor flavor,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
    {
        // Weave Legacy is an OpenCode 1 plugin.
        if (flavor != WeaveFlavor.Weave)
            return null;

        var trial = await TryAsync(ownerUserId, files, ct).ConfigureAwait(false);
        var loaded = trial.Agents.Where(IsWeaveAgent).Select(agent => agent.Id!).ToList();
        return loaded.Count > 0
            ? new WeaveCheck(true, loaded)
            : new WeaveCheck(false, [], "OpenCode 2 started, but Weave added no agents. Weave ignores the whole file when it can't read it.");
    }

    /// <summary>
    /// The owner saved: writes the folder, then reloads each of their servers that reads it. Servers that read the
    /// user's own files (the owner switched where the config lives) are replaced once they're idle, as for any setting.
    /// </summary>
    public async Task ConfigChangedAsync(string ownerUserId, CancellationToken ct)
    {
        var folder = await GetConfigFolderAsync(ownerUserId).ConfigureAwait(false);
        var servers = runningServers(ownerUserId).Where(server => folder is not null && server.Setup.WeaveConfigFolder == folder).ToList();
        await ReloadAsync(ownerUserId, servers, ct).ConfigureAwait(false);
    }

    /// <summary>Weave went into the owner's plugin list, or out of it: every running server of theirs reads it again.</summary>
    public Task PluginsChangedAsync(string ownerUserId, CancellationToken ct)
        => ReloadAsync(ownerUserId, runningServers(ownerUserId), ct);

    public WeaveApplyStatus? GetApplyStatus(string ownerUserId) =>
        _reloads.TryGetValue(ownerUserId, out var run) ? run.Status() : null;

    private async Task ReloadAsync(string ownerUserId, IReadOnlyList<OpenCode2Server> servers, CancellationToken ct)
    {
        var run = new ReloadRun(servers);
        if (_reloads.TryGetValue(ownerUserId, out var previous))
            await previous.Cancellation.CancelAsync().ConfigureAwait(false);
        _reloads[ownerUserId] = run;

        await run.PassAsync(logger, ct).ConfigureAwait(false);
        if (!run.Done)
            _ = Task.Run(() => run.RetryUntilDoneAsync(logger), CancellationToken.None);
    }

    private sealed record Trial(IReadOnlyList<OpenCode2PluginInfo> Plugins, IReadOnlyList<OpenCode2AgentInfo> Agents, IReadOnlyList<string> Log);

    /// <summary>
    /// Writes <paramref name="files"/> into the owner's trial folder and starts a throwaway server that reads it, with
    /// the owner's own config and Fleet's, then loads an empty folder and returns what V2 loaded there and its log. One
    /// try at a time per owner: they share the folder.
    /// </summary>
    private async Task<Trial> TryAsync(string ownerUserId, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var gate = _trialLocks.GetOrAdd(ownerUserId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = WeaveConfigFolder.TrialFor(dataDirectory(), ownerUserId, OpenCode2HarnessSession.Type);
            var project = WeaveConfigFolder.TrialProjectFor(dataDirectory(), ownerUserId, OpenCode2HarnessSession.Type);
            WeaveConfigFolder.Mirror(folder, files);
            Directory.CreateDirectory(project);

            var log = new ConcurrentQueue<string>();
            var server = await startTrial(ownerUserId, folder, log.Enqueue, ct).ConfigureAwait(false);
            await using (server.ConfigureAwait(false))
            {
                await server.LoadLocationAsync(project, ct).ConfigureAwait(false);
                var plugins = await server.Client.GetPluginsAsync(project, ct).ConfigureAwait(false);

                // A Weave the config lists is either loaded or has failed once V2 is done with it.
                var listed = ListedPlugins(await server.Client.GetConfigAsync(project, ct).ConfigureAwait(false))
                    .Where(entry => PackageOf(entry) == Package)
                    .ToList();
                var pluginsUntil = DateTimeOffset.UtcNow + PluginWait;
                while (listed.Count > 0
                       && !plugins.Any(plugin => Describe(plugin)?.Package == Package)
                       && !FailedPlugins(log).Any(failed => PackageOf(failed.Target) == Package)
                       && DateTimeOffset.UtcNow < pluginsUntil)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    plugins = await server.Client.GetPluginsAsync(project, ct).ConfigureAwait(false);
                }

                var agents = await server.Client.GetAgentsAsync(project, ct).ConfigureAwait(false);

                // Weave adds its agents once it has read its config, which can be after V2 says the folder is loaded.
                if (plugins.Any(plugin => Describe(plugin)?.Package == Package))
                {
                    var until = DateTimeOffset.UtcNow + AgentWait;
                    while (!agents.Any(IsWeaveAgent) && DateTimeOffset.UtcNow < until)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                        agents = await server.Client.GetAgentsAsync(project, ct).ConfigureAwait(false);
                    }
                }

                await Task.Delay(LogGrace, ct).ConfigureAwait(false);
                return new Trial(plugins, agents, [.. log]);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WeaveConfig?> LoadConfigAsync(string userId)
    {
        using var userScope = BackgroundUserContext.BeginScope(userId);
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<IWeaveConfigRepository>() is { } configs
            ? await configs.GetAsync().ConfigureAwait(false)
            : null;
    }

    internal static bool IsWeaveAgent(OpenCode2AgentInfo agent)
        => agent.Id is not null && agent.Description?.StartsWith(WeaveManagedMarker, StringComparison.Ordinal) == true;

    /// <summary>
    /// The npm package a loaded plugin comes from and its entry as Fleet shows it: <c>package@version</c> as the config
    /// names it, or a local build's folder as a <c>file://</c> URL, named by the nearest <c>package.json</c>.
    /// V2's own plugins are left out.
    /// </summary>
    internal static (string? Package, string Entry)? Describe(OpenCode2PluginInfo plugin)
    {
        switch (plugin.Source)
        {
            case { Type: "package", Target: { Length: > 0 } target }:
                return (PackageOf(target), target);
            case { Type: "local", Path: { Length: > 0 } path }:
                var folder = Path.GetDirectoryName(path) ?? path;
                return (PackageNameAt(folder), new Uri(folder).AbsoluteUri);
            default:
                return null;
        }
    }

    /// <summary>
    /// The plugin entries in the config V2 read (<c>GET /api/config</c>): each document's <c>plugins</c>, as a package or
    /// path, or <c>{ "package": … }</c> with options.
    /// </summary>
    internal static IReadOnlyList<string> ListedPlugins(IEnumerable<OpenCode2ConfigSource> sources)
    {
        var entries = new List<string>();
        foreach (var source in sources)
        {
            if (source.Info.ValueKind != JsonValueKind.Object
                || !source.Info.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind == JsonValueKind.String && plugin.GetString() is { Length: > 0 } entry)
                    entries.Add(entry);
                else if (plugin.ValueKind == JsonValueKind.Object && plugin.TryGetProperty("package", out var package)
                         && package.ValueKind == JsonValueKind.String && package.GetString() is { Length: > 0 } named)
                    entries.Add(named);
            }
        }

        return entries;
    }

    /// <summary>
    /// The npm package a plugin entry names: <c>@scope/name@1.2.3</c> and <c>name@next</c> lose the version; a path or
    /// <c>file://</c> URL is named by the nearest <c>package.json</c>.
    /// </summary>
    internal static string? PackageOf(string entry)
    {
        if (entry.StartsWith("file://", StringComparison.Ordinal))
            return Uri.TryCreate(entry, UriKind.Absolute, out var uri) ? PackageNameAt(uri.LocalPath) : null;
        if (Path.IsPathFullyQualified(entry))
            return PackageNameAt(entry);

        var versionAt = entry.LastIndexOf('@');
        return versionAt > 0 ? entry[..versionAt] : entry;
    }

    private static string? PackageNameAt(string path)
    {
        for (var directory = path; directory is not null; directory = Path.GetDirectoryName(directory))
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
    /// The plugins V2 couldn't load, from its log: <c>message="failed to load plugin" target=… cause="…"</c>. The cause
    /// loses V2's <c>Cause([Fail(…)])</c> wrapping.
    /// </summary>
    internal static IReadOnlyList<(string Target, string Cause)> FailedPlugins(IEnumerable<string> log)
    {
        var failed = new List<(string, string)>();
        foreach (var line in log)
        {
            if (!line.Contains("failed to load plugin", StringComparison.Ordinal) || FailedPluginLine().Match(line) is not { Success: true } match)
                continue;

            var cause = match.Groups["cause"].Value.Replace("\\n", " ", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
            if (CauseWrapping().Match(cause) is { Success: true } inner)
                cause = inner.Groups["inner"].Value;
            failed.Add((match.Groups["target"].Value, WhiteSpace().Replace(cause, " ").Trim()));
        }

        return failed;
    }

    [GeneratedRegex(@"target=(?<target>\S+).*?cause=""(?<cause>(?:[^""\\]|\\.)*)""", RegexOptions.None, "en-US")]
    private static partial Regex FailedPluginLine();

    [GeneratedRegex(@"^Cause\(\[Fail\((?<inner>.*)\)\]\)$", RegexOptions.Singleline, "en-US")]
    private static partial Regex CauseWrapping();

    [GeneratedRegex(@"\s+", RegexOptions.None, "en-US")]
    private static partial Regex WhiteSpace();

    /// <summary>One change's reloads: which servers are done, and the loop that waits for the ones with a question open.</summary>
    private sealed partial class ReloadRun(IReadOnlyList<OpenCode2Server> servers)
    {
        private readonly ConcurrentDictionary<OpenCode2Server, bool> _reloaded = new(servers.Select(s => KeyValuePair.Create(s, false)));
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private string? _error;

        public CancellationTokenSource Cancellation { get; } = new();

        public bool Done => _reloaded.Values.All(reloaded => reloaded);

        public WeaveApplyStatus Status()
        {
            // One row per folder a session runs in: done when every server with a session there has reloaded.
            var folders = _reloaded
                .SelectMany(pair => pair.Key.SessionFolders.Select(folder => (Folder: folder, Reloaded: pair.Value)))
                .GroupBy(row => row.Folder, StringComparer.Ordinal)
                .Select(group => new WeaveApplyFolder(group.Key, group.All(row => row.Reloaded)))
                .OrderBy(folder => folder.Directory, StringComparer.Ordinal)
                .ToList();
            return new WeaveApplyStatus(folders, _error);
        }

        public async Task PassAsync(ILogger logger, CancellationToken ct)
        {
            foreach (var (server, reloaded) in _reloaded)
            {
                if (reloaded || Cancellation.IsCancellationRequested)
                    continue;

                // A server that stopped has nothing to reload: the next one reads the config when it starts.
                if (!server.IsRunning)
                {
                    _reloaded[server] = true;
                    continue;
                }

                try
                {
                    if (await WaitsOnQuestionAsync(server, ct).ConfigureAwait(false))
                        continue;

                    await server.Client.ReloadLocationsAsync(ct).ConfigureAwait(false);
                    _reloaded[server] = true;
                    LogServerReloaded(logger, server.ProcessId ?? 0);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    _error = $"Couldn't reload OpenCode 2: {ex.Message}";
                    LogServerReloadFailed(logger, ex, server.ProcessId ?? 0);
                }
            }
        }

        /// <summary>Whether a session on <paramref name="server"/> waits on a question, which a reload would cancel.</summary>
        private static async Task<bool> WaitsOnQuestionAsync(OpenCode2Server server, CancellationToken ct)
        {
            foreach (var sessionId in await server.Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false))
            {
                if ((await server.Client.GetFormsAsync(sessionId, ct).ConfigureAwait(false)).Count > 0)
                    return true;
            }

            return false;
        }

        public async Task RetryUntilDoneAsync(ILogger logger)
        {
            var ct = Cancellation.Token;
            try
            {
                while (!Done && !ct.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow - _started > ReloadGiveUpAfter)
                    {
                        _error = "A question stayed open for 30 minutes. OpenCode 2 picks the change up when its server restarts.";
                        return;
                    }

                    await Task.Delay(ReloadPollInterval, ct).ConfigureAwait(false);
                    await PassAsync(logger, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A newer change took over.
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Reloaded OpenCode 2 server {ProcessId}'s folders for a Weave change")]
        private static partial void LogServerReloaded(ILogger logger, int processId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reload OpenCode 2 server {ProcessId}'s folders for a Weave change")]
        private static partial void LogServerReloadFailed(ILogger logger, Exception ex, int processId);
    }
}
