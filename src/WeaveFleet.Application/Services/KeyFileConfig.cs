using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Services;

public sealed record KeyFileGroup(
    string Id,
    int Priority,
    [property: JsonPropertyName("extensions")] string[]? Extensions,
    [property: JsonPropertyName("fileNames")] string[]? FileNames,
    string[] CompatibleTools,
    string[]? Trumps);

public sealed record KeyFileConfig(KeyFileGroup[] Groups)
{
    private static KeyFileConfig? _instance;

    public static KeyFileConfig Load()
    {
        if (_instance is not null)
            return _instance;

        var assembly = typeof(KeyFileConfig).Assembly;
        const string ResourceName = "WeaveFleet.Application.Resources.key-file-types.json";

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

        _instance = JsonSerializer.Deserialize(stream, KeyFileConfigJsonContext.Default.KeyFileConfig)
            ?? throw new InvalidOperationException("Failed to deserialize key-file-types.json.");

        return _instance;
    }
}

[JsonSerializable(typeof(KeyFileConfig))]
[JsonSerializable(typeof(KeyFileGroup))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class KeyFileConfigJsonContext : JsonSerializerContext;
