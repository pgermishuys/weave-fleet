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
/// </summary>
public sealed partial class WeaveConfigService(
    IWeaveConfigRepository configs,
    IHarnessRegistry registry,
    IUserContext userContext,
    FleetOptions options,
    WeaveDetectionCache detections,
    TimeProvider timeProvider,
    ILogger<WeaveConfigService> logger)
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
                found.Add(installs is null
                    ? new WeaveHarnessDetection(harness.Type, harness.DisplayName, false, [],
                        $"Fleet doesn't hand Weave a config in {harness.DisplayName} yet.")
                    : new WeaveHarnessDetection(harness.Type, harness.DisplayName, true, installs));
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
}
