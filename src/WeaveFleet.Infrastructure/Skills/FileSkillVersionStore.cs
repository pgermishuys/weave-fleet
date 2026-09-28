using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Skills;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// Keeps each user's versions of the built-in skills under <c>{data}/skills/{user}/{skill}/</c>:
/// <list type="bullet">
/// <item><c>skill.json</c>: the versions, why each was made, and which one sessions get.</item>
/// <item><c>v{n}/{skill}/SKILL.md</c>: each version's text, never changed once written. <c>v{n}</c> is the folder a
/// harness loads, so a new version is a new folder and never changes a skill under a running session.</item>
/// <item><c>fleet/{hash}.md</c>: Fleet's version when one of theirs was saved, to show what Fleet changed since.</item>
/// </list>
/// Fleet's own startup sync writes elsewhere, so nothing here is ever overwritten by an update.
/// </summary>
public sealed class FileSkillVersionStore(string root) : ISkillVersionStore, IDisposable
{
    private const string IndexFile = "skill.json";
    private const string SkillFile = "SKILL.md";

    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileSkillVersionStore(FleetOptions options)
        : this(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) ?? Environment.CurrentDirectory, "skills"))
    {
    }

    public void Dispose() => _lock.Dispose();

    public Task<SkillVersionHistory> GetAsync(string userId, string name, CancellationToken ct = default)
        => LockedAsync(() => ReadIndex(userId, name), ct);

    public Task<IReadOnlyList<SkillVersionHistory>> ListAsync(string userId, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            var folder = UserFolder(userId);
            if (!Directory.Exists(folder))
                return (IReadOnlyList<SkillVersionHistory>)[];

            return Directory.EnumerateDirectories(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(IsValidName)
                .Select(name => ReadIndex(userId, name))
                .Where(history => history.Versions.Count > 0)
                .OrderBy(history => history.Name, StringComparer.Ordinal)
                .ToList();
        }, ct);

    public Task<SkillVersion> AddAsync(
        string userId, string name, string content, SkillVersionSource source, string fleetContent, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(() =>
        {
            var history = ReadIndex(userId, name);
            var fleetHash = KeepFleet(userId, name, fleetContent);
            var version = new SkillVersion(
                history.Versions.Count == 0 ? 1 : history.Versions.Max(v => v.Number) + 1,
                DateTimeOffset.UtcNow,
                Trimmed(source.Note),
                Trimmed(source.SessionId),
                Trimmed(source.SessionTitle),
                fleetHash);

            WriteAtomically(Path.Combine(FolderFor(userId, name, version.Number), name, SkillFile), SkillVersions.Normalize(content));
            // A new version is based on Fleet's current one, so what the user kept over before no longer matters.
            WriteIndex(userId, history with { Active = version.Number, KeptFleetHash = null, Versions = [.. history.Versions, version] });
            return version;
        }, ct);
    }

    public Task SetActiveAsync(string userId, string name, int? number, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(() =>
        {
            var history = ReadIndex(userId, name);
            if (number is { } n && history.Versions.All(v => v.Number != n))
                throw new ArgumentException($"{name} has no version {n}.", nameof(number));
            WriteIndex(userId, history with { Active = number });
            return true;
        }, ct);
    }

    public Task KeepOverAsync(string userId, string name, string fleetContent, CancellationToken ct = default)
    {
        ThrowIfInvalid(name);
        return LockedAsync(() =>
        {
            var history = ReadIndex(userId, name);
            WriteIndex(userId, history with { KeptFleetHash = KeepFleet(userId, name, fleetContent) });
            return true;
        }, ct);
    }

    public Task<string?> ReadAsync(string userId, string name, int number, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            if (!IsValidName(name))
                return null;
            var path = Path.Combine(FolderFor(userId, name, number), name, SkillFile);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, ct);

    public Task<string?> ReadFleetAsync(string userId, string name, string fleetHash, CancellationToken ct = default)
        => LockedAsync(() =>
        {
            if (!IsValidName(name) || !IsHex(fleetHash))
                return null;
            var path = Path.Combine(SkillFolder(userId, name), "fleet", $"{fleetHash}.md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, ct);

    public string FolderFor(string userId, string name, int number)
    {
        ThrowIfInvalid(name);
        return Path.Combine(SkillFolder(userId, name), $"v{number}");
    }

    /// <summary>Keeps a copy of Fleet's version, once per hash, and returns the hash.</summary>
    private string KeepFleet(string userId, string name, string fleetContent)
    {
        var hash = SkillVersions.Hash(fleetContent);
        var path = Path.Combine(SkillFolder(userId, name), "fleet", $"{hash}.md");
        if (!File.Exists(path))
            WriteAtomically(path, SkillVersions.Normalize(fleetContent));
        return hash;
    }

    private SkillVersionHistory ReadIndex(string userId, string name)
    {
        if (!IsValidName(name))
            return SkillVersionHistory.Empty(name);

        var path = Path.Combine(SkillFolder(userId, name), IndexFile);
        if (!File.Exists(path))
            return SkillVersionHistory.Empty(name);

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), SkillVersionJsonContext.Default.SkillIndex);
            var versions = (file?.Versions ?? []).Where(v => v is { Number: > 0, FleetHash: not null }).OrderBy(v => v.Number).ToList();
            var active = file?.Active is { } number && versions.Any(v => v.Number == number) ? number : (int?)null;
            return new SkillVersionHistory(name, active, file?.KeptFleetHash, versions);
        }
        catch (JsonException)
        {
            // A file someone broke by hand: sessions get Fleet's version until it's fixed. The versions' text is still
            // there, in the v{n} folders.
            return SkillVersionHistory.Empty(name);
        }
    }

    private void WriteIndex(string userId, SkillVersionHistory history)
    {
        var file = new SkillIndex { Active = history.Active, KeptFleetHash = history.KeptFleetHash, Versions = [.. history.Versions] };
        WriteAtomically(
            Path.Combine(SkillFolder(userId, history.Name), IndexFile),
            JsonSerializer.Serialize(file, SkillVersionJsonContext.Default.SkillIndex));
    }

    private async Task<T> LockedAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LockedAsync(Func<bool> action, CancellationToken ct) => await LockedAsync<bool>(action, ct).ConfigureAwait(false);

    private string UserFolder(string userId)
        => Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16]);

    private string SkillFolder(string userId, string name) => Path.Combine(UserFolder(userId), name);

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Skill names are lowercase letters, digits and dashes, which keeps them from naming a path elsewhere.</summary>
    internal static bool IsValidName(string? name)
        => name is { Length: > 0 and <= 64 } && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && name[0] != '-';

    private static bool IsHex(string value) => value.Length is > 0 and <= 64 && value.All(char.IsAsciiHexDigitLower);

    private static void ThrowIfInvalid(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"'{name}' isn't a skill name.", nameof(name));
    }
}

/// <summary>What <c>skill.json</c> holds.</summary>
internal sealed class SkillIndex
{
    public int? Active { get; set; }
    public string? KeptFleetHash { get; set; }
    public List<SkillVersion>? Versions { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SkillIndex))]
internal sealed partial class SkillVersionJsonContext : JsonSerializerContext;
