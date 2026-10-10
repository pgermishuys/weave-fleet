using System.Text.Json;

namespace WeaveFleet.Application.Mods;

/// <summary>One kept version of a mod. Its <c>v{n}/</c> folder never changes once written.</summary>
/// <param name="Number">Fleet's number: 1 for the first version kept, counting up.</param>
/// <param name="Version">The manifest's own <c>version</c> string when it was kept.</param>
/// <param name="Sha256">Of the hooks module, hex, as the check report gives it.</param>
/// <param name="SessionId">The session whose draft it was.</param>
/// <param name="Note">Why it was kept, in the user's words; null when they didn't say.</param>
/// <param name="Check">The check report at Keep, as the checker returned it (a <c>CheckReport</c>); null when none ran.</param>
public sealed record ModVersion(
    int Number,
    DateTimeOffset CreatedAt,
    string Version,
    string Sha256,
    string? SessionId,
    string? SessionTitle,
    string? Note,
    JsonElement? Check);

/// <summary>Why a mod, or a draft, is off.</summary>
/// <param name="By"><see cref="ModOffBy.User"/> or <see cref="ModOffBy.Strikes"/>.</param>
/// <param name="Error">With <see cref="ModOffBy.Strikes"/>: the last failure.</param>
public sealed record ModOff(string By, DateTimeOffset At, string? Error = null);

public static class ModOffBy
{
    public const string User = "user";

    /// <summary>Three failures in a row, not answered by <c>.catch</c>.</summary>
    public const string Strikes = "strikes";
}

/// <summary>A user's kept mod: its versions, the active one, and whether it's off. What <c>versions.json</c> holds.</summary>
/// <param name="Active">The version sessions load; null only when nothing has been kept.</param>
/// <param name="Off">Null when the mod is on.</param>
public sealed record ModHistory(string Name, int? Active, ModOff? Off, IReadOnlyList<ModVersion> Versions)
{
    public static ModHistory Empty(string name) => new(name, null, null, []);

    public ModVersion? ActiveVersion => Active is { } number ? Versions.FirstOrDefault(v => v.Number == number) : null;
}

/// <summary>A mod the agent is writing in one session: <c>drafts/{sessionId}/{name}/</c>.</summary>
/// <param name="Folder">The draft's folder, holding <c>mod.json</c>.</param>
/// <param name="Off">Null when the draft is on in its session.</param>
public sealed record ModDraft(string SessionId, string Name, string Folder, ModOff? Off);

/// <summary>Where a kept version came from.</summary>
public sealed record ModKeepSource(string? SessionTitle, string? Note, JsonElement? Check);

/// <summary>What a mod's <c>mod.json</c> says (<c>docs/mods/api.md</c>, "A mod on disk").</summary>
/// <param name="Hooks">The hooks module, relative to <c>mod.json</c>.</param>
public sealed record ModManifest(string Name, string Version, string Description, string Hooks);

/// <summary>A file of a version or a draft, for Show code. Paths are relative to the mod's folder, with <c>/</c>.</summary>
public sealed record ModFile(string Path, string Content);

/// <summary>A draft that can't be kept, or a version or mod that isn't there. The message says why, for the user.</summary>
public sealed class ModStoreException(string message) : Exception(message);

/// <summary>A <c>$.store</c> write that would take the mod's store past <see cref="ModStoreLimits.StoreBytes"/>.</summary>
public sealed class ModStoreFullException(string message) : Exception(message);

public static class ModStoreLimits
{
    /// <summary><c>$.store</c> per mod per user: 4 MiB of JSON (<c>Limits.storeBytes</c>).</summary>
    public const int StoreBytes = 4 * 1024 * 1024;

    /// <summary>Show code skips files larger than this, and anything that isn't UTF-8 text.</summary>
    public const int ShownFileBytes = 512 * 1024;
}

/// <summary>
/// Keeps each user's mods under <c>{data}/mods/{user16}/</c>: kept mods with immutable versions, the agent's drafts per
/// session, and each mod's <c>$.store</c>. Layout as in <c>docs/mods/api.md</c>, "Where mods live".
/// </summary>
public interface IModVersionStore
{
    /// <summary>Every mod the user has kept at least one version of, by name.</summary>
    Task<IReadOnlyList<ModHistory>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>The mod's history; <see cref="ModHistory.Empty"/> when it has none.</summary>
    Task<ModHistory> GetAsync(string userId, string name, CancellationToken ct = default);

    /// <summary>
    /// Copies the session's draft into the next <c>v{n}/</c>, makes it active, turns the mod on and removes the draft.
    /// Throws <see cref="ModStoreException"/> when there's no such draft or its <c>mod.json</c> is missing, invalid, names
    /// another mod or a hooks module that isn't in the folder.
    /// </summary>
    Task<ModVersion> KeepAsync(string userId, string sessionId, string name, ModKeepSource source, CancellationToken ct = default);

    /// <summary>Makes <paramref name="number"/> the active version. Throws <see cref="ModStoreException"/> when it doesn't exist.</summary>
    Task SetActiveAsync(string userId, string name, int number, CancellationToken ct = default);

    /// <summary>Turns a kept mod off (<paramref name="off"/>) or on (null).</summary>
    Task SetOffAsync(string userId, string name, ModOff? off, CancellationToken ct = default);

    /// <summary>The folder of a kept version, holding its <c>mod.json</c>.</summary>
    string VersionFolder(string userId, string name, int number);

    /// <summary>
    /// The <c>mod.json</c> in <paramref name="folder"/> (a version's or a draft's); null when it's missing, isn't JSON, or
    /// lacks a field. Doesn't check the name or the hooks path; Keep does.
    /// </summary>
    Task<ModManifest?> ReadManifestAsync(string folder, CancellationToken ct = default);

    /// <summary>A version's files, text only; null when the version doesn't exist.</summary>
    Task<IReadOnlyList<ModFile>?> ReadVersionFilesAsync(string userId, string name, int number, CancellationToken ct = default);

    // ── Drafts ──────────────────────────────────────────────────────────

    /// <summary>The session's drafts, by name. A folder without a <c>mod.json</c> is still listed (the agent is writing it).</summary>
    Task<IReadOnlyList<ModDraft>> ListDraftsAsync(string userId, string sessionId, CancellationToken ct = default);

    /// <summary>The draft, or null when the session has none by that name.</summary>
    Task<ModDraft?> GetDraftAsync(string userId, string sessionId, string name, CancellationToken ct = default);

    /// <summary>Where the session's draft of <paramref name="name"/> lives (whether or not it exists yet), for the agent's tools.</summary>
    string DraftFolder(string userId, string sessionId, string name);

    /// <summary>Turns a draft off in its session (<paramref name="off"/>), or back on (null) when the agent reloads it.</summary>
    Task SetDraftOffAsync(string userId, string sessionId, string name, ModOff? off, CancellationToken ct = default);

    /// <summary>A draft's files, text only; null when the draft doesn't exist.</summary>
    Task<IReadOnlyList<ModFile>?> ReadDraftFilesAsync(string userId, string sessionId, string name, CancellationToken ct = default);

    // ── $.store: per user, per mod, shared by a draft and its kept mod ──

    Task<JsonElement?> GetValueAsync(string userId, string name, string key, CancellationToken ct = default);

    /// <summary>Throws <see cref="ModStoreFullException"/> when the store would pass <see cref="ModStoreLimits.StoreBytes"/>.</summary>
    Task SetValueAsync(string userId, string name, string key, JsonElement value, CancellationToken ct = default);

    Task DeleteValueAsync(string userId, string name, string key, CancellationToken ct = default);

    /// <summary>The store's keys, sorted ordinally.</summary>
    Task<IReadOnlyList<string>> KeysAsync(string userId, string name, CancellationToken ct = default);
}

/// <summary>
/// Runs the static check on a mod's folder (<c>docs/mods/api.md</c>, "The static check"). The mod host implements it
/// (M2); until then Fleet keeps mods without a report.
/// </summary>
public interface IModChecker
{
    /// <summary>The <c>CheckReport</c> as JSON, or null when no checker is available.</summary>
    Task<JsonElement?> CheckAsync(string folder, CancellationToken ct = default);
}

/// <summary>Keeps mods without a check report: Fleet's checker until the mod host is in.</summary>
public sealed class NoModChecker : IModChecker
{
    public Task<JsonElement?> CheckAsync(string folder, CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
}

/// <summary>Mod names, as <c>mod.json</c> and the folders use them.</summary>
public static class ModNames
{
    /// <summary>
    /// Lowercase letters, digits and <c>-</c>, 1 to 64, starting with a letter; <c>fleet-</c> is Fleet's, and
    /// <c>drafts</c> is the drafts folder beside the kept mods.
    /// </summary>
    public static bool IsValid(string? name)
        => name is { Length: > 0 and <= 64 }
           && name[0] is >= 'a' and <= 'z'
           && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
           && !name.StartsWith("fleet-", StringComparison.Ordinal)
           && name != "drafts";

    /// <summary>Session ids name a folder too: letters, digits, <c>-</c> and <c>_</c>, up to 128.</summary>
    public static bool IsValidSessionId(string? sessionId)
        => sessionId is { Length: > 0 and <= 128 } && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
