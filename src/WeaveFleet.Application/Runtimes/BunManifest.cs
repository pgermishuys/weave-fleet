using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace WeaveFleet.Application.Runtimes;

/// <summary>
/// Fleet's Bun manifest, <c>bun.json</c>: the Bun Fleet recommends, the oldest version that's safe to run, a note, and
/// each platform's archive and sha256. It's kept in weave-fleet as <c>bun/bun.json</c>, built into Fleet as the
/// fallback (<see cref="BunRelease.Pinned"/>), and published to Fleet's release repository, where Fleet reads it on
/// its update schedule. Parsed strictly: anything unexpected is refused, never guessed at.
/// </summary>
/// <example><code>
/// { "schema": 1, "version": "1.4.3", "oldestSafe": "1.4.3", "note": "Bun 1.4.3 fixes …",
///   "assets": { "linux-x64": { "name": "bun-linux-x64-baseline.zip", "sha256": "…64 hex…", "size": 36646949 }, … } }
/// </code></example>
public static class BunManifest
{
    /// <summary>The only <c>schema</c> this Fleet reads.</summary>
    public const int Schema = 1;

    /// <summary>The most bytes a manifest may have; a bigger one is refused unread.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// Every platform Fleet ships, with the one Bun asset name it accepts for it (Bun's own release naming; the x64
    /// builds are the <c>-baseline</c> ones). A manifest must name exactly these.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssetNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["linux-x64"] = "bun-linux-x64-baseline.zip",
        ["linux-arm64"] = "bun-linux-aarch64.zip",
        ["osx-x64"] = "bun-darwin-x64-baseline.zip",
        ["osx-arm64"] = "bun-darwin-aarch64.zip",
        ["win-x64"] = "bun-windows-x64-baseline.zip",
        ["win-arm64"] = "bun-windows-aarch64.zip",
    };

    /// <summary>The deepest JSON nesting a manifest may have; a real one is three levels.</summary>
    private const int MaxDepth = 8;

    private const int MaxNoteLength = 280;

    private static readonly string[] TopLevelFields = ["schema", "version", "oldestSafe", "note", "assets"];

    private static readonly string[] AssetFields = ["name", "sha256", "size"];

    private const long MaxAssetSize = 512L * 1024 * 1024;

    /// <summary>
    /// Reads a manifest. On success <paramref name="release"/> is the release it describes; otherwise
    /// <paramref name="error"/> says, in a sentence, the first thing wrong with it.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> json, [NotNullWhen(true)] out BunRelease? release, [NotNullWhen(false)] out string? error)
    {
        release = null;
        if (json.Length > MaxBytes)
        {
            error = $"The manifest is bigger than {MaxBytes / 1024} KB.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException exception)
        {
            error = exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase)
                ? $"The manifest is nested too deeply (at most {MaxDepth} levels)."
                : "The manifest isn't valid JSON (comments and trailing commas aren't allowed).";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The manifest must be a JSON object.";
                return false;
            }

            if (!CheckFields(root, TopLevelFields, "", out error))
                return false;

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || schema.GetRawText() != Schema.ToString(CultureInfo.InvariantCulture))
            {
                error = $"'schema' must be the number {Schema}.";
                return false;
            }

            if (!TryVersion(root, "version", out var version, out error)
                || !TryVersion(root, "oldestSafe", out var oldestSafe, out error))
                return false;

            if (BunVersion.Parse(oldestSafe) > BunVersion.Parse(version))
            {
                error = $"'oldestSafe' ({oldestSafe}) can't be newer than 'version' ({version}).";
                return false;
            }

            if (!TryNote(root, out var note, out error) || !TryAssets(root, out var assets, out error))
                return false;

            release = new BunRelease(version, assets) { OldestSafe = oldestSafe, Note = note };
            error = null;
            return true;
        }
    }

    /// <summary>Refuses a field that isn't allowed and one that appears twice, since <see cref="JsonDocument"/> allows both.</summary>
    private static bool CheckFields(JsonElement element, IEnumerable<string> allowed, string path, [NotNullWhen(false)] out string? error)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
            {
                error = $"'{path}{property.Name}' isn't a field a manifest has.";
                return false;
            }

            if (!seen.Add(property.Name))
            {
                error = $"'{path}{property.Name}' appears more than once.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryVersion(JsonElement root, string field, [NotNullWhen(true)] out string? version, [NotNullWhen(false)] out string? error)
    {
        version = null;
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String
            || !BunVersion.TryParse(element.GetString(), out var parsed))
        {
            error = $"'{field}' must be a Bun version such as 1.4.2.";
            return false;
        }

        if (parsed.PreRelease is not null)
        {
            error = $"'{field}' can't be a pre-release.";
            return false;
        }

        if (parsed < BunVersion.Parse(BunRelease.MinimumVersion))
        {
            error = $"'{field}' can't be older than {BunRelease.MinimumVersion}.";
            return false;
        }

        version = element.GetString()!;
        error = null;
        return true;
    }

    private static bool TryNote(JsonElement root, out string? note, [NotNullWhen(false)] out string? error)
    {
        note = null;
        error = null;
        if (!root.TryGetProperty("note", out var element)
            || (element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.String))
        {
            error = "'note' must be a string, or null.";
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
            return true;

        var text = element.GetString()!;
        var trimmed = text.Trim();
        if (trimmed.Length is < 1 or > MaxNoteLength || text.Any(char.IsControl))
        {
            error = $"'note' must be 1 to {MaxNoteLength} characters with no control characters, or null.";
            return false;
        }

        note = trimmed;
        return true;
    }

    private static bool TryAssets(JsonElement root, [NotNullWhen(true)] out List<BunAsset>? assets, [NotNullWhen(false)] out string? error)
    {
        assets = null;
        if (!root.TryGetProperty("assets", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            error = "'assets' must be an object with an entry for each platform.";
            return false;
        }

        if (!CheckFields(element, AssetNames.Keys, "assets.", out error))
            return false;

        var result = new List<BunAsset>(AssetNames.Count);
        foreach (var (rid, expectedName) in AssetNames)
        {
            var path = $"assets.{rid}";
            if (!element.TryGetProperty(rid, out var asset) || asset.ValueKind != JsonValueKind.Object)
            {
                error = $"'{path}' must be an object with a name, a sha256 and a size.";
                return false;
            }

            if (!CheckFields(asset, AssetFields, path + ".", out error))
                return false;

            if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != expectedName)
            {
                error = $"'{path}.name' must be '{expectedName}'.";
                return false;
            }

            if (!asset.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !IsLowercaseSha256(sha.GetString()!))
            {
                error = $"'{path}.sha256' must be 64 lowercase hex characters.";
                return false;
            }

            if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                || !long.TryParse(size.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                || bytes is < 1 or > MaxAssetSize)
            {
                error = $"'{path}.size' must be a whole number of bytes from 1 to {MaxAssetSize}.";
                return false;
            }

            result.Add(new BunAsset(rid, expectedName, sha.GetString()!) { Size = bytes });
        }

        assets = result;
        error = null;
        return true;
    }

    private static bool IsLowercaseSha256(string text) =>
        text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
