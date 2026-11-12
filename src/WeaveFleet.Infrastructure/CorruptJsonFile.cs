using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure;

/// <summary>
/// Reads a JSON file Fleet wrote. A file that no longer parses (a crash or a full disk mid-write, a hand edit) is moved
/// aside as <c>name.json.corrupt-{timestamp}</c> and logged, and the caller starts again from an empty value, instead
/// of failing every request that touches the file.
/// </summary>
internal static partial class CorruptJsonFile
{
    public static T Read<T>(string path, JsonTypeInfo<T> info, Func<T> empty, ILogger? logger) where T : class
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return empty();
        }

        try
        {
            var value = JsonSerializer.Deserialize(json, info);
            if (value is not null)
                return value;
        }
        catch (JsonException)
        {
        }

        SetAside(path, logger);
        return empty();
    }

    private static void SetAside(string path, ILogger? logger)
    {
        var aside = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, aside, overwrite: true);
            if (logger is not null)
                LogSetAside(logger, path, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (logger is not null)
                LogSetAsideFailed(logger, path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{File} isn't valid JSON; moved it to {Aside} and starting from an empty one")]
    private static partial void LogSetAside(ILogger logger, string file, string aside);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{File} isn't valid JSON and couldn't be moved aside; starting from an empty one")]
    private static partial void LogSetAsideFailed(ILogger logger, string file, Exception ex);
}
