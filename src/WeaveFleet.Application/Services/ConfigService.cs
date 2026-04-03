using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Reads and writes weave config files (weave-opencode.jsonc).
/// </summary>
public sealed partial class ConfigService(ILogger<ConfigService> logger)
{
    private static readonly string UserConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string UserConfigPath =
        Path.Combine(UserConfigDir, "weave-opencode.jsonc");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Returns the path to the user config file (may not exist yet).</summary>
    public static string UserConfigFilePath => UserConfigPath;

    /// <summary>Reads the user-level config. Returns empty object if file doesn't exist.</summary>
    public async Task<JsonObject> GetUserConfigAsync(CancellationToken ct = default)
    {
        if (!File.Exists(UserConfigPath))
        {
            LogConfigNotFound(UserConfigPath);
            return [];
        }

        var json = await File.ReadAllTextAsync(UserConfigPath, ct).ConfigureAwait(false);
        return ParseJsonc(json) ?? [];
    }

    /// <summary>
    /// Returns a merged config: project-level config (if directory provided) overlaid on user config.
    /// </summary>
    public async Task<JsonObject> GetMergedConfigAsync(string? directory = null, CancellationToken ct = default)
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
    public async Task UpdateUserConfigAsync(JsonObject config, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UserConfigDir);
        var json = config.ToJsonString(SerializerOptions);
        await File.WriteAllTextAsync(UserConfigPath, json, ct).ConfigureAwait(false);
        LogConfigWritten(UserConfigPath);
    }

    /// <summary>Returns the paths relevant for display/debugging.</summary>
    public static ConfigPaths GetConfigPaths() => new(UserConfigDir, UserConfigPath);

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
