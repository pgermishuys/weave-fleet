using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NuCode.Configuration;

/// <summary>
/// Loads NuCode configuration from three layers (lowest to highest priority):
/// 1. Global: ~/.nucode/config.jsonc (or %APPDATA%\nucode\config.jsonc on Windows)
/// 2. Project: ./nucode.jsonc or ./.nucode/config.jsonc (searched from working directory)
/// 3. Programmatic: values from <see cref="NuCodeOptions.Config"/>
/// Later layers override earlier ones via deep merge.
/// </summary>
internal sealed class ConfigLoader : IConfigLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _workingDirectory;
    private readonly NuCodeConfig? _programmaticConfig;
    private readonly ILogger<ConfigLoader>? _logger;

    internal ConfigLoader(string workingDirectory, NuCodeConfig? programmaticConfig, ILoggerFactory? loggerFactory)
    {
        _workingDirectory = workingDirectory;
        _programmaticConfig = programmaticConfig;
        _logger = loggerFactory?.CreateLogger<ConfigLoader>();
    }

    internal ConfigLoader(string workingDirectory, NuCodeConfig? programmaticConfig)
    {
        _workingDirectory = workingDirectory;
        _programmaticConfig = programmaticConfig;
    }

    public NuCodeConfig Load()
    {
        var global = LoadGlobalConfig();
        var project = LoadProjectConfig();
        var result = Merge(global, project);
        result = Merge(result, _programmaticConfig);
        return result;
    }

    /// <summary>
    /// Returns the global config file path for the current platform.
    /// Windows: %APPDATA%\nucode\config.jsonc
    /// Linux/macOS: ~/.nucode/config.jsonc
    /// </summary>
    internal static string GetGlobalConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "nucode", "config.jsonc");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".nucode", "config.jsonc");
    }

    /// <summary>
    /// Searches for project config starting from working directory.
    /// Looks for: ./nucode.jsonc, ./.nucode/config.jsonc
    /// Returns null if no project config file exists.
    /// </summary>
    internal static string? FindProjectConfigPath(string workingDirectory)
    {
        var candidate1 = Path.Combine(workingDirectory, "nucode.jsonc");
        if (File.Exists(candidate1))
        {
            return candidate1;
        }

        var candidate2 = Path.Combine(workingDirectory, ".nucode", "config.jsonc");
        if (File.Exists(candidate2))
        {
            return candidate2;
        }

        return null;
    }

    private NuCodeConfig? LoadGlobalConfig()
    {
        var path = GetGlobalConfigPath();
        return LoadConfigFile(path, "global");
    }

    private NuCodeConfig? LoadProjectConfig()
    {
        var path = FindProjectConfigPath(_workingDirectory);
        if (path is null)
        {
            return null;
        }
        return LoadConfigFile(path, "project");
    }

    private NuCodeConfig? LoadConfigFile(string path, string layerName)
    {
        if (!File.Exists(path))
        {
            _logger?.LogDebug("No {Layer} config file at {Path}", layerName, path);
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            var config = JsoncParser.Deserialize<NuCodeConfig>(text, SerializerOptions);
            _logger?.LogDebug("Loaded {Layer} config from {Path}", layerName, path);
            return config;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse {Layer} config at {Path}", layerName, path);
            return null;
        }
    }

    /// <summary>
    /// Deep-merges two config objects. Values from <paramref name="overlay"/> override <paramref name="base"/>.
    /// Dictionaries are merged key-by-key. Lists from overlay replace base lists entirely (except Instructions which concatenate).
    /// Null overlay values do not override base values.
    /// </summary>
    internal static NuCodeConfig Merge(NuCodeConfig? @base, NuCodeConfig? overlay)
    {
        if (@base is null && overlay is null)
        {
            return new NuCodeConfig();
        }
        if (@base is null)
        {
            return overlay!;
        }
        if (overlay is null)
        {
            return @base;
        }

        return new NuCodeConfig
        {
            Model = overlay.Model ?? @base.Model,
            SmallModel = overlay.SmallModel ?? @base.SmallModel,
            DefaultAgent = overlay.DefaultAgent ?? @base.DefaultAgent,
            LogLevel = overlay.LogLevel ?? @base.LogLevel,
            Snapshot = overlay.Snapshot ?? @base.Snapshot,
            EnabledProviders = overlay.EnabledProviders ?? @base.EnabledProviders,
            DisabledProviders = overlay.DisabledProviders ?? @base.DisabledProviders,
            Plugins = MergePlugins(@base.Plugins, overlay.Plugins),
            Instructions = ConcatLists(@base.Instructions, overlay.Instructions),
            Agents = MergeDictionaries(@base.Agents, overlay.Agents),
            Permission = MergePermission(@base.Permission, overlay.Permission),
            Mcp = MergeDictionaries(@base.Mcp, overlay.Mcp),
            Provider = MergeDictionaries(@base.Provider, overlay.Provider),
            Compaction = overlay.Compaction ?? @base.Compaction,
            Experimental = overlay.Experimental ?? @base.Experimental,
        };
    }

    /// <summary>
    /// Merges two dictionaries. Keys from overlay override base. New keys from overlay are added.
    /// </summary>
    private static Dictionary<string, TValue>? MergeDictionaries<TValue>(
        Dictionary<string, TValue>? @base,
        Dictionary<string, TValue>? overlay)
    {
        if (@base is null)
        {
            return overlay;
        }
        if (overlay is null)
        {
            return @base;
        }

        var result = new Dictionary<string, TValue>(@base, StringComparer.Ordinal);
        foreach (var (key, value) in overlay)
        {
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Merges permission configs. Rules from overlay override base rules by key.
    /// </summary>
    private static PermissionConfig? MergePermission(PermissionConfig? @base, PermissionConfig? overlay)
    {
        if (@base is null)
        {
            return overlay;
        }
        if (overlay is null)
        {
            return @base;
        }

        return new PermissionConfig
        {
            Rules = MergeDictionaries(@base.Rules, overlay.Rules),
        };
    }

    /// <summary>
    /// Merges plugin lists: deduplicates by value, overlay entries override base.
    /// </summary>
    private static List<string>? MergePlugins(List<string>? @base, List<string>? overlay)
    {
        if (@base is null)
        {
            return overlay;
        }
        if (overlay is null)
        {
            return @base;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // Add overlay first (higher priority)
        foreach (var plugin in overlay)
        {
            if (seen.Add(plugin))
            {
                result.Add(plugin);
            }
        }
        // Add base entries not already present
        foreach (var plugin in @base)
        {
            if (seen.Add(plugin))
            {
                result.Add(plugin);
            }
        }

        return result;
    }

    /// <summary>
    /// Concatenates two lists (used for instructions — all instructions from all layers are kept).
    /// </summary>
    private static List<string>? ConcatLists(List<string>? @base, List<string>? overlay)
    {
        if (@base is null)
        {
            return overlay;
        }
        if (overlay is null)
        {
            return @base;
        }

        var result = new List<string>(@base.Count + overlay.Count);
        result.AddRange(@base);
        result.AddRange(overlay);
        return result;
    }
}
