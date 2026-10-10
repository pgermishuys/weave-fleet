namespace WeaveFleet.Application.Runtimes;

/// <summary>The preference a user's own Bun path is stored under.</summary>
public static class BunPathPreference
{
    /// <summary>The user preference key holding the path of the user's own Bun; empty or missing when none.</summary>
    public const string Key = "ModsBunPath";
}

/// <summary>
/// Which Bun runs a user's mods, when it isn't Fleet's own: the machine owner's <c>Fleet:Harness:BunPath</c>, which
/// wins over everything, or the path the user chose in Settings, which is theirs alone. A singleton can read any
/// user's choice.
/// </summary>
public interface IBunPathSetting
{
    /// <summary><c>Fleet:Harness:BunPath</c> from configuration, or <see langword="null"/> when it's empty.</summary>
    string? FromConfiguration { get; }

    /// <summary>The path that applies to <paramref name="userId"/>: the configured one, else the user's saved one; <see langword="null"/> when neither.</summary>
    Task<string?> GetAsync(string userId, CancellationToken ct);

    /// <summary>Saves <paramref name="userId"/>'s own Bun path, or clears it when <paramref name="path"/> is null or blank. Not allowed while <see cref="FromConfiguration"/> is set.</summary>
    Task SaveAsync(string userId, string? path, CancellationToken ct);
}
