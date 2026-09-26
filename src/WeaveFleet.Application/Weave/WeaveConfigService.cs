using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Weave;

/// <summary>The Weave section of Settings: what each harness loads, the config Fleet keeps, and the last save.</summary>
/// <param name="Source"><c>own</c> or <c>fleet</c>.</param>
public sealed record WeaveConfigView(
    string Source,
    IReadOnlyDictionary<string, string> Files,
    string? UpdatedAt,
    IReadOnlyList<WeaveHarnessDetection> Harnesses,
    WeaveApplyStatus? Apply);

/// <summary>A draft tried by one harness, for one Weave.</summary>
public sealed record WeaveHarnessCheck(string HarnessType, string HarnessName, WeaveFlavor Flavor, WeaveCheck Check);

/// <summary>What a save did. When a check failed nothing was saved, and <paramref name="Config"/> is null.</summary>
public sealed record WeaveSaveResult(bool Saved, IReadOnlyList<WeaveHarnessCheck> Checks, WeaveConfigView? Config);

/// <summary>The user's own Weave files, read so they can start Fleet's config from them.</summary>
/// <param name="Path">The config file that was read, or would be, for display.</param>
public sealed record WeaveOwnConfig(WeaveFlavor Flavor, string Path, IReadOnlyDictionary<string, string> Files);

/// <summary>What each harness had loaded when Fleet last asked, per user. Asking can start a process, so it's kept.</summary>
public sealed class WeaveDetectionCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<WeaveHarnessDetection>> _byUser = new(StringComparer.Ordinal);

    public bool TryGet(string userId, out IReadOnlyList<WeaveHarnessDetection> detections) =>
        _byUser.TryGetValue(userId, out detections!);

    public void Set(string userId, IReadOnlyList<WeaveHarnessDetection> detections) => _byUser[userId] = detections;
}

/// <summary>
/// Keeps the user's Weave config and hands it to the harnesses. Fleet never edits the user's own Weave files: with
/// source <see cref="WeaveConfigSource.Fleet"/> each harness points Weave at a folder Fleet writes. A save is tried by
/// the harness first, so a config that would leave Weave with no agents is never used.
/// Add Weave is the one edit Fleet makes to the user's files: it puts the Weave adapter in a harness's plugin list,
/// when the user asks and the harness has no Weave, and remembers the entry so it takes out only what it put in.
/// </summary>
public sealed partial class WeaveConfigService(
    IWeaveConfigRepository configs,
    IHarnessRegistry registry,
    IUserContext userContext,
    FleetOptions options,
    WeaveDetectionCache detections,
    TimeProvider timeProvider,
    ILogger<WeaveConfigService> logger,
    IUserPreferenceRepository? preferences = null,
    IWeavePackageVersions? versions = null)
{
    public async Task<Result<WeaveConfigView>> GetAsync(bool redetect, CancellationToken ct)
    {
        var config = await configs.GetAsync().ConfigureAwait(false);
        var harnesses = await DetectAsync(redetect, ct).ConfigureAwait(false);
        return ToView(config, harnesses);
    }

    /// <summary>
    /// The user's own Weave files as Weave reads them outside Fleet: <c>~/.weave/config.weave</c> with its
    /// <c>prompts/</c>, or Legacy's <c>~/.config/opencode/weave-opencode.jsonc</c>. Not with sign-in on, where the
    /// home folder is the host's, not the user's.
    /// </summary>
    public Result<WeaveOwnConfig> ReadOwn(WeaveFlavor flavor)
    {
        if (options.Auth.Enabled)
            return FleetError.ValidationError("Weave.Own", "Fleet can't read your own Weave files when it runs with sign-in.");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flavor == WeaveFlavor.Legacy)
        {
            var folder = Path.Combine(home, ".config", "opencode");
            var jsonc = Path.Combine(folder, WeaveConfigFiles.LegacyConfig);
            var json = Path.ChangeExtension(jsonc, ".json");
            var path = File.Exists(jsonc) ? jsonc : File.Exists(json) ? json : jsonc;
            if (File.Exists(path))
                files[WeaveConfigFiles.LegacyConfig] = File.ReadAllText(path);
            return new WeaveOwnConfig(flavor, path, files);
        }

        var root = Path.Combine(home, ".weave");
        var config = Path.Combine(root, WeaveConfigFiles.Config);
        if (File.Exists(config))
            files[WeaveConfigFiles.Config] = File.ReadAllText(config);

        var prompts = Path.Combine(root, WeaveConfigFiles.PromptsFolder.TrimEnd('/'));
        if (Directory.Exists(prompts))
        {
            foreach (var file in Directory.EnumerateFiles(prompts, "*.md").Order(StringComparer.Ordinal))
            {
                var relative = WeaveConfigFiles.PromptsFolder + Path.GetFileName(file);
                if (WeaveConfigFiles.RejectPath(relative) is null && files.Count < WeaveConfigFiles.MaxFiles)
                    files[relative] = File.ReadAllText(file);
            }
        }

        return new WeaveOwnConfig(flavor, config, files);
    }

    /// <summary>Tries draft files with every harness that loads <paramref name="flavor"/>, without saving them.</summary>
    public async Task<Result<IReadOnlyList<WeaveHarnessCheck>>> CheckAsync(
        WeaveFlavor flavor,
        IReadOnlyDictionary<string, string>? files,
        CancellationToken ct)
    {
        files ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (WeaveConfigFiles.Reject(files) is { } rejected)
            return FleetError.ValidationError("Weave.Files", rejected);
        if (!files.ContainsKey(WeaveConfigFiles.ConfigFor(flavor)))
            return FleetError.ValidationError("Weave.Files", $"There's no {WeaveConfigFiles.ConfigFor(flavor)} to try.");

        return Result.Success(await RunChecksAsync(flavor, files, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Saves the source and files. With source <c>fleet</c>, each Weave config in <paramref name="files"/> is tried
    /// first by the harnesses that load that Weave, and nothing is saved if any of them fails.
    /// </summary>
    public async Task<Result<WeaveSaveResult>> SaveAsync(
        string? source,
        IReadOnlyDictionary<string, string>? files,
        CancellationToken ct)
    {
        var parsedSource = source switch
        {
            "own" => WeaveConfigSource.Own,
            "fleet" => WeaveConfigSource.Fleet,
            _ => (WeaveConfigSource?)null,
        };
        if (parsedSource is null)
            return FleetError.ValidationError("Weave.Source", "Source must be 'own' or 'fleet'.");

        files ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (WeaveConfigFiles.Reject(files) is { } rejected)
            return FleetError.ValidationError("Weave.Files", rejected);

        var checks = new List<WeaveHarnessCheck>();
        if (parsedSource == WeaveConfigSource.Fleet)
        {
            foreach (var flavor in new[] { WeaveFlavor.Weave, WeaveFlavor.Legacy })
            {
                if (files.ContainsKey(WeaveConfigFiles.ConfigFor(flavor)))
                    checks.AddRange(await RunChecksAsync(flavor, files, ct).ConfigureAwait(false));
            }

            if (checks.Any(check => !check.Check.Ok))
                return new WeaveSaveResult(false, checks, null);
        }

        var config = new WeaveConfig
        {
            Source = parsedSource.Value,
            Files = new Dictionary<string, string>(files, StringComparer.Ordinal),
            UpdatedAt = timeProvider.GetUtcNow().ToString("O"),
        };
        await configs.SaveAsync(config).ConfigureAwait(false);

        foreach (var harness in registry.GetAll())
        {
            if (registry.GetRuntimeByType(harness.Type) is not { } runtime)
                continue;
            try
            {
                await runtime.WeaveConfigChangedAsync(userContext.UserId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogApplyFailed(ex, harness.Type);
            }
        }

        var harnesses = await DetectAsync(redetect: false, ct).ConfigureAwait(false);
        return new WeaveSaveResult(true, checks, ToView(config, harnesses));
    }

    /// <summary>
    /// Puts the Weave adapter, at its newest version on npm, into <paramref name="harnessType"/>'s plugin list, has the
    /// harness load it, and asks the harness again which Weave it loads.
    /// </summary>
    public async Task<Result<WeavePluginChange>> AddPluginAsync(string harnessType, CancellationToken ct)
    {
        var target = PluginTarget(harnessType);
        if (target.IsFailure)
            return target.Error;
        var (runtime, home, name) = target.Value;

        var detection = (await DetectAsync(redetect: false, ct).ConfigureAwait(false)).FirstOrDefault(h => h.HarnessType == harnessType);
        if (detection is { Installs.Count: > 0 })
            return Conflict($"{name} already has Weave in its plugin list.");

        var chosen = ChooseConfigFile(home);
        if (chosen.IsFailure)
            return chosen.Error;
        var path = chosen.Value;

        string text;
        try
        {
            text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "{}\n";
            var listed = WeavePluginList.Read(text, home.ListKey);
            if (listed.FirstOrDefault(entry => PackageOf(entry) == home.Package) is { } existing)
            {
                return Conflict(
                    $"{path} already lists {existing}, but {name} didn't load it. Check {name}'s log, or fix the entry by hand.");
            }
        }
        catch (FormatException ex)
        {
            return FleetError.ValidationError("Weave.Plugin", $"Fleet can't edit {path}: {ex.Message}");
        }

        if (versions is null || await versions.NewestAsync(home.Package, ct).ConfigureAwait(false) is not { } version)
            return FleetError.ValidationError("Weave.Plugin", $"Fleet couldn't look up {home.Package} on npm. Check the connection and try again.");

        var entry = $"{home.Package}@{version}";
        await WriteConfigAsync(path, WeavePluginList.Add(text, home.ListKey, entry), ct).ConfigureAwait(false);
        await RememberAddedAsync(harnessType, path, entry).ConfigureAwait(false);
        LogPluginAdded(entry, path);

        var view = await ReloadAndDetectAsync(runtime, harnessType, ct).ConfigureAwait(false);
        var install = view.Harnesses.FirstOrDefault(h => h.HarnessType == harnessType)?.Installs
            .FirstOrDefault(i => i.Package == home.Package);
        var message = install switch
        {
            { Error: { } error } => $"Added {entry} to {path}, but {name} couldn't load it: {error}",
            not null => $"Added {entry} to {path}. {name} loaded it.",
            null => $"Added {entry} to {path}. {name} loads it when its sessions next start.",
        };
        return new WeavePluginChange(path, entry, install is { Error: null }, message, view);
    }

    /// <summary>Takes the entry Add Weave put in <paramref name="harnessType"/>'s plugin list out again.</summary>
    public async Task<Result<WeavePluginChange>> RemovePluginAsync(string harnessType, CancellationToken ct)
    {
        var target = PluginTarget(harnessType);
        if (target.IsFailure)
            return target.Error;
        var (runtime, home, name) = target.Value;

        if (await ReadAddedAsync(harnessType).ConfigureAwait(false) is not { } added)
            return FleetError.ValidationError("Weave.Plugin", $"Fleet didn't add Weave to {name}, so it leaves {name}'s plugin list alone.");

        string? edited;
        try
        {
            edited = File.Exists(added.Path)
                ? WeavePluginList.Remove(await File.ReadAllTextAsync(added.Path, ct).ConfigureAwait(false), home.ListKey, added.Entry)
                : null;
        }
        catch (FormatException ex)
        {
            return FleetError.ValidationError("Weave.Plugin", $"Fleet can't edit {added.Path}: {ex.Message}");
        }

        if (edited is not null)
            await WriteConfigAsync(added.Path, edited, ct).ConfigureAwait(false);
        await ForgetAddedAsync(harnessType).ConfigureAwait(false);
        LogPluginRemoved(added.Entry, added.Path);

        var view = await ReloadAndDetectAsync(runtime, harnessType, ct).ConfigureAwait(false);
        var gone = view.Harnesses.FirstOrDefault(h => h.HarnessType == harnessType)?.Installs.All(i => i.Entry != added.Entry) ?? true;
        var message = edited is null
            ? $"{added.Entry} wasn't in {added.Path} any more, so there was nothing to take out."
            : gone
                ? $"Took {added.Entry} out of {added.Path}."
                : $"Took {added.Entry} out of {added.Path}. {name} drops it when its sessions next start.";
        return new WeavePluginChange(added.Path, added.Entry, gone, message, view);
    }

    private Result<(IHarnessRuntime Runtime, WeavePluginHome Home, string Name)> PluginTarget(string harnessType)
    {
        if (options.Auth.Enabled)
            return FleetError.ValidationError("Weave.Plugin", "Fleet can't change a harness's plugins when it runs with sign-in.");

        var harness = registry.GetAll().FirstOrDefault(h => h.Type == harnessType);
        if (harness is null || registry.GetRuntimeByType(harnessType) is not { } runtime)
            return FleetError.NotFoundFor("Harness", harnessType);
        if (runtime.GetWeavePluginHome() is not { } home)
            return FleetError.ValidationError("Weave.Plugin", $"Fleet can't add Weave to {harness.DisplayName}.");

        return (runtime, home, harness.DisplayName);
    }

    /// <summary>The config file Add Weave edits: the one that's there, or the last candidate when none is.</summary>
    private static Result<string> ChooseConfigFile(WeavePluginHome home)
    {
        var existing = home.ConfigFiles.Select(file => Path.Combine(home.ConfigFolder, file)).Where(File.Exists).ToList();
        return existing.Count switch
        {
            0 => Path.Combine(home.ConfigFolder, home.ConfigFiles[^1]),
            1 => existing[0],
            _ => Conflict(
                $"{home.ConfigFolder} has {string.Join(" and ", existing.Select(Path.GetFileName))}, and Fleet doesn't know which one you use. Keep one, then try again."),
        };
    }

    /// <summary>Replaces the file in one step, so the harness never reads half of it.</summary>
    private static async Task WriteConfigAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, text, ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<WeaveConfigView> ReloadAndDetectAsync(IHarnessRuntime runtime, string harnessType, CancellationToken ct)
    {
        try
        {
            await runtime.WeavePluginsChangedAsync(userContext.UserId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogApplyFailed(ex, harnessType);
        }

        var config = await configs.GetAsync().ConfigureAwait(false);
        return ToView(config, await DetectAsync(redetect: true, ct).ConfigureAwait(false));
    }

    /// <summary>The npm package a plugin entry names: <c>@scope/name@1.2.3</c> loses its version.</summary>
    internal static string PackageOf(string entry)
    {
        var versionAt = entry.LastIndexOf('@');
        return versionAt > 0 ? entry[..versionAt] : entry;
    }

    private static FleetError Conflict(string message) => new(FleetError.Conflict.Code, message);

    private sealed record AddedPlugin(string Path, string Entry);

    private static string AddedKey(string harnessType, string part) => $"weave.added.{harnessType}.{part}";

    private async Task RememberAddedAsync(string harnessType, string path, string entry)
    {
        if (preferences is null)
            return;
        await preferences.SetAsync(AddedKey(harnessType, "path"), path).ConfigureAwait(false);
        await preferences.SetAsync(AddedKey(harnessType, "entry"), entry).ConfigureAwait(false);
    }

    private async Task<AddedPlugin?> ReadAddedAsync(string harnessType)
    {
        if (preferences is null)
            return null;
        var path = await preferences.GetAsync(AddedKey(harnessType, "path")).ConfigureAwait(false);
        var entry = await preferences.GetAsync(AddedKey(harnessType, "entry")).ConfigureAwait(false);
        return string.IsNullOrEmpty(path) || string.IsNullOrEmpty(entry) ? null : new AddedPlugin(path, entry);
    }

    private async Task ForgetAddedAsync(string harnessType)
    {
        if (preferences is null)
            return;
        await preferences.SetAsync(AddedKey(harnessType, "path"), string.Empty).ConfigureAwait(false);
        await preferences.SetAsync(AddedKey(harnessType, "entry"), string.Empty).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<WeaveHarnessCheck>> RunChecksAsync(
        WeaveFlavor flavor,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
    {
        // Legacy's JSONC is caught here, with its line, before any harness starts.
        if (flavor == WeaveFlavor.Legacy
            && WeaveConfigFiles.CheckLegacyJson(files[WeaveConfigFiles.LegacyConfig]) is { } jsonError)
        {
            return [new WeaveHarnessCheck("fleet", "Fleet", flavor, new WeaveCheck(false, [], jsonError))];
        }

        var harnesses = await DetectAsync(redetect: false, ct).ConfigureAwait(false);
        var checks = new List<WeaveHarnessCheck>();
        foreach (var harness in harnesses.Where(h => h.Installs.Any(install => install.Flavor == flavor && install.AcceptsFleetConfig)))
        {
            if (registry.GetRuntimeByType(harness.HarnessType) is not { } runtime)
                continue;

            WeaveCheck? check;
            try
            {
                check = await runtime.CheckWeaveConfigAsync(userContext.UserId, flavor, files, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(ex, harness.HarnessType);
                check = new WeaveCheck(false, [], $"{harness.HarnessName} didn't start with this config: {ex.Message}");
            }

            if (check is not null)
                checks.Add(new WeaveHarnessCheck(harness.HarnessType, harness.HarnessName, flavor, check));
        }

        return checks;
    }

    private async Task<IReadOnlyList<WeaveHarnessDetection>> DetectAsync(bool redetect, CancellationToken ct)
    {
        if (!redetect && detections.TryGet(userContext.UserId, out var cached))
            return cached;

        var availability = await registry.GetAvailabilityAsync(ct).ConfigureAwait(false);
        var found = new List<WeaveHarnessDetection>();
        // Harnesses that aren't set up on this machine aren't listed: there's nothing to hand a config to.
        foreach (var harness in availability.Where(harness => harness.Available))
        {
            if (registry.GetRuntimeByType(harness.Type) is not { } runtime)
                continue;

            try
            {
                var installs = await runtime.DetectWeaveAsync(userContext.UserId, ct).ConfigureAwait(false);
                if (installs is null)
                {
                    found.Add(new WeaveHarnessDetection(harness.Type, harness.DisplayName, false, [],
                        $"Fleet doesn't hand Weave a config in {harness.DisplayName} yet."));
                    continue;
                }

                var added = await ReadAddedAsync(harness.Type).ConfigureAwait(false);
                var marked = installs.Select(install => install with { AddedByFleet = install.Entry == added?.Entry }).ToList();
                // With two config files there, Add Weave is still offered, and says why it won't pick one.
                string? addTo = null;
                if (marked.Count == 0 && !options.Auth.Enabled && runtime.GetWeavePluginHome() is { } home)
                    addTo = ChooseConfigFile(home) is { IsSuccess: true } file ? file.Value : home.ConfigFolder;
                found.Add(new WeaveHarnessDetection(harness.Type, harness.DisplayName, true, marked, AddTo: addTo));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDetectFailed(ex, harness.Type);
                found.Add(new WeaveHarnessDetection(harness.Type, harness.DisplayName, false, [],
                    $"Fleet couldn't ask {harness.DisplayName} which plugins it loads: {ex.Message}"));
            }
        }

        detections.Set(userContext.UserId, found);
        return found;
    }

    private WeaveConfigView ToView(WeaveConfig config, IReadOnlyList<WeaveHarnessDetection> harnesses)
    {
        WeaveApplyStatus? apply = null;
        foreach (var harness in registry.GetAll())
        {
            if (registry.GetRuntimeByType(harness.Type)?.GetWeaveApplyStatus(userContext.UserId) is { } status)
            {
                apply = apply is null
                    ? status
                    : new WeaveApplyStatus([.. apply.Folders, .. status.Folders], apply.Error ?? status.Error);
            }
        }

        return new WeaveConfigView(
            config.Source == WeaveConfigSource.Fleet ? "fleet" : "own",
            config.Files,
            config.UpdatedAt,
            harnesses,
            apply);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't ask harness {HarnessType} which Weave it loads")]
    private partial void LogDetectFailed(Exception ex, string harnessType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Harness {HarnessType} couldn't try a Weave config")]
    private partial void LogCheckFailed(Exception ex, string harnessType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Harness {HarnessType} couldn't apply the saved Weave config")]
    private partial void LogApplyFailed(Exception ex, string harnessType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Added Weave plugin {Entry} to {Path}")]
    private partial void LogPluginAdded(string entry, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Took Weave plugin {Entry} out of {Path}")]
    private partial void LogPluginRemoved(string entry, string path);
}
