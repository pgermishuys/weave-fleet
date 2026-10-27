using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Memory.Dreaming;

namespace WeaveFleet.Infrastructure.Memory;

/// <summary>
/// Keeps each dream as one JSON file, <c>memory/{user}/dreams/{dreamId}.json</c> in Fleet's data folder (beside the notes),
/// written to a temporary file first so a crash never leaves half a dream.
/// </summary>
internal sealed partial class FileDreamStore(FleetOptions options, ILogger<FileDreamStore> logger) : IDreamStore, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<Dream>> ListAsync(string userId, string repository, int limit, CancellationToken ct = default)
    {
        var folder = Folder(userId);
        if (!Directory.Exists(folder))
            return [];

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.json")
                .Select(Read)
                .OfType<Dream>()
                .Where(dream => string.Equals(dream.Repository, repository, PathComparison))
                .OrderByDescending(dream => dream.Started)
                .Take(limit)];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Dream?> FindAsync(string userId, string id, CancellationToken ct = default)
    {
        if (!IsValidId(id))
            return null;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Read(Path.Combine(Folder(userId), $"{id}.json"));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(string userId, Dream dream, CancellationToken ct = default)
    {
        if (!IsValidId(dream.Id))
            throw new ArgumentException($"'{dream.Id}' isn't a dream id.", nameof(dream));
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(Folder(userId), $"{dream.Id}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(dream, DreamJsonContext.Default.Dream), ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Dream? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), DreamJsonContext.Default.Dream) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogUnreadable(logger, path, ex);
            return null;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Same user folder as the notes' (<see cref="FileMemoryStore"/>).</summary>
    private string Folder(string userId)
    {
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) ?? Environment.CurrentDirectory;
        return Path.Combine(data, "memory", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16], "dreams");
    }

    private static bool IsValidId(string? id) => FileMemoryStore.IsValidId(id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped dream file {Path}: Fleet can't read it")]
    private static partial void LogUnreadable(ILogger logger, string path, Exception exception);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dream))]
internal sealed partial class DreamJsonContext : JsonSerializerContext
{
}
