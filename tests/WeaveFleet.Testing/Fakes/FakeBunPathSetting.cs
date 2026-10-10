using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Testing.Fakes;

/// <summary>An in-memory <see cref="IBunPathSetting"/>: the configured path (if any) and each user's saved one.</summary>
public sealed class FakeBunPathSetting(string? fromConfiguration = null) : IBunPathSetting
{
    private readonly Dictionary<string, string> _saved = new(StringComparer.Ordinal);

    /// <summary>The machine owner's path, as <c>Fleet:Harness:BunPath</c> sets it; null when none.</summary>
    public string? FromConfiguration { get; set; } = string.IsNullOrWhiteSpace(fromConfiguration) ? null : fromConfiguration;

    /// <summary>Every save in order: the user, and the path (null when cleared).</summary>
    public List<(string UserId, string? Path)> Saves { get; } = [];

    /// <summary>Puts a user's saved path in place without counting it as a save.</summary>
    public void Seed(string userId, string path) => _saved[userId] = path;

    public Task<string?> GetAsync(string userId, CancellationToken ct)
        => Task.FromResult(FromConfiguration ?? _saved.GetValueOrDefault(userId));

    public Task SaveAsync(string userId, string? path, CancellationToken ct)
    {
        Saves.Add((userId, path));
        if (string.IsNullOrWhiteSpace(path))
            _saved.Remove(userId);
        else
            _saved[userId] = path;
        return Task.CompletedTask;
    }
}
