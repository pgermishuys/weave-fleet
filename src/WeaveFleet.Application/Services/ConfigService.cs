using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Reads and writes weave config files (weave-opencode.jsonc).
/// </summary>
public sealed partial class ConfigService
{
    private static readonly string DefaultUserConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string DefaultUserConfigPath =
        Path.Combine(DefaultUserConfigDir, "weave-opencode.jsonc");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly ConfigPaths _configPaths;
    private readonly ILogger<ConfigService> _logger;

    /// <summary>Initialises the service with the default user config path.</summary>
    public ConfigService(ILogger<ConfigService> logger)
        : this(logger, CreateDefaultConfigPaths())
    {
    }

    /// <summary>Initialises the service with explicit config paths.</summary>
    public ConfigService(ILogger<ConfigService> logger, ConfigPaths configPaths)
    {
        _logger = logger;
        _configPaths = configPaths;
    }

    /// <summary>Returns the path to the default user config file (may not exist yet).</summary>
    public static string UserConfigFilePath => DefaultUserConfigPath;

    /// <summary>Reads the user-level config. Returns empty object if file doesn't exist.</summary>
    public Task<JsonObject> GetUserConfigAsync()
        => GetUserConfigAsync(CancellationToken.None);

    /// <summary>Reads the user-level config. Returns empty object if file doesn't exist.</summary>
    public async Task<JsonObject> GetUserConfigAsync(CancellationToken ct)
    {
        if (!File.Exists(_configPaths.UserConfigPath))
        {
            LogConfigNotFound(_configPaths.UserConfigPath);
            return [];
        }

        var json = await File.ReadAllTextAsync(_configPaths.UserConfigPath, ct).ConfigureAwait(false);
        return ParseJsonc(json) ?? [];
    }

    /// <summary>Returns a merged config: user-level config only.</summary>
    public Task<JsonObject> GetMergedConfigAsync()
        => GetMergedConfigAsync(null, CancellationToken.None);

    /// <summary>Returns a merged config: project-level config overlaid on user config.</summary>
    public Task<JsonObject> GetMergedConfigAsync(string directory)
        => GetMergedConfigAsync(directory, CancellationToken.None);

    /// <summary>Returns a merged config: user-level config only.</summary>
    public Task<JsonObject> GetMergedConfigAsync(CancellationToken ct)
        => GetMergedConfigAsync(null, ct);

    /// <summary>
    /// Returns a merged config: project-level config (if directory provided) overlaid on user config.
    /// </summary>
    public async Task<JsonObject> GetMergedConfigAsync(string? directory, CancellationToken ct)
    {
        var userConfig = await GetUserConfigAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(directory))
            return userConfig;

        var projectConfigPath = Path.Combine(directory, "weave-opencode.jsonc");
        if (!File.Exists(projectConfigPath))
            return userConfig;

        var projectJson = await File.ReadAllTextAsync(projectConfigPath, ct).ConfigureAwait(false);
        var projectConfig = ParseJsonc(projectJson);
        if (projectConfig is null)
            return userConfig;

        return MergeObjects(userConfig, projectConfig);
    }

    /// <summary>Writes the user-level config file.</summary>
    public Task UpdateUserConfigAsync(JsonObject config)
        => UpdateUserConfigAsync(config, CancellationToken.None);

    /// <summary>Writes the user-level config file.</summary>
    public async Task UpdateUserConfigAsync(JsonObject config, CancellationToken ct)
    {
        Directory.CreateDirectory(_configPaths.ConfigDirectory);
        var json = config.ToJsonString(SerializerOptions);
        await File.WriteAllTextAsync(_configPaths.UserConfigPath, json, ct).ConfigureAwait(false);
        LogConfigWritten(_configPaths.UserConfigPath);
    }

    /// <summary>Returns the paths relevant for display/debugging.</summary>
    public ConfigPaths GetConfigPaths() => _configPaths;

    /// <summary>Returns the default paths relevant for display/debugging.</summary>
    public static ConfigPaths CreateDefaultConfigPaths() => new(DefaultUserConfigDir, DefaultUserConfigPath);

    // ── Private helpers ────────────────────────────────────────────────────────

    private static JsonObject? ParseJsonc(string text)
    {
        try
        {
            return JsonNode.Parse(text, null, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shallow merge: project values override user values at the top level.
    /// Deep objects are recursively merged; scalars and arrays are replaced by overlay.
    /// </summary>
    private static JsonObject MergeObjects(JsonObject baseObj, JsonObject overlay)
    {
        var result = new JsonObject();

        foreach (var (key, value) in baseObj)
        {
            result[key] = value?.DeepClone();
        }

        foreach (var (key, value) in overlay)
        {
            if (result.TryGetPropertyValue(key, out var existing)
                && existing is JsonObject existingObj
                && value is JsonObject overlayObj)
            {
                result[key] = MergeObjects(existingObj, overlayObj);
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "User config not found at {Path}, returning empty config")]
    private partial void LogConfigNotFound(string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "User config written to {Path}")]
    private partial void LogConfigWritten(string path);
}

/// <summary>Config file paths for display.</summary>
public sealed record ConfigPaths(string ConfigDirectory, string UserConfigPath);
