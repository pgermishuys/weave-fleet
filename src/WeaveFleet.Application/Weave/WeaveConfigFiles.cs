using System.Text.Json;

namespace WeaveFleet.Application.Weave;

/// <summary>
/// The files a Fleet-kept Weave config can hold, by their path in the folder Fleet points Weave at, and the checks
/// Fleet can run on them without a harness.
/// </summary>
public static class WeaveConfigFiles
{
    /// <summary>Weave's config. Weave reads it from the folder in <see cref="WeaveEnvironment.GlobalConfigDir"/>.</summary>
    public const string Config = "config.weave";

    /// <summary>Weave Legacy's config. Legacy reads it from the folder in <see cref="WeaveEnvironment.LegacyConfigDir"/>.</summary>
    public const string LegacyConfig = "weave-opencode.jsonc";

    /// <summary>Weave looks for <c>prompt_file</c> and <c>prompt_append_file</c> here, next to <see cref="Config"/>.</summary>
    public const string PromptsFolder = "prompts/";

    public const int MaxFileLength = 128 * 1024;
    public const int MaxFiles = 50;

    /// <summary>The file Fleet keeps for <paramref name="flavor"/>.</summary>
    public static string ConfigFor(WeaveFlavor flavor) => flavor == WeaveFlavor.Legacy ? LegacyConfig : Config;

    /// <summary>
    /// Why <paramref name="path"/> can't be kept, or null when it can: <see cref="Config"/>, <see cref="LegacyConfig"/>,
    /// or a Markdown file directly in <see cref="PromptsFolder"/>.
    /// </summary>
    public static string? RejectPath(string path)
    {
        if (path is Config or LegacyConfig)
            return null;

        if (path.StartsWith(PromptsFolder, StringComparison.Ordinal))
        {
            var name = path[PromptsFolder.Length..];
            if (name.Length is > 0 and <= 100
                && name.EndsWith(".md", StringComparison.Ordinal)
                && name.Length > ".md".Length
                && !name.StartsWith('.')
                && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                && !name.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }

            return $"'{path}' isn't a prompt file name Fleet accepts: letters, digits, '-', '_' and '.', ending in .md.";
        }

        return $"'{path}' isn't a file Fleet keeps for Weave. Use {Config}, {LegacyConfig} or {PromptsFolder}<name>.md.";
    }

    /// <summary>Why <paramref name="files"/> can't be kept, or null when they can.</summary>
    public static string? Reject(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count > MaxFiles)
            return $"A Weave config can have at most {MaxFiles} files.";

        foreach (var (path, content) in files)
        {
            if (RejectPath(path) is { } rejected)
                return rejected;
            if (content.Length > MaxFileLength)
                return $"{path} is longer than {MaxFileLength / 1024} KB.";
        }

        return null;
    }

    /// <summary>
    /// Parses Weave Legacy's JSONC the way Legacy does (comments and trailing commas allowed), so a typo is caught with
    /// its line before the harness sees it. Null when it parses; otherwise the problem, with its line and column.
    /// </summary>
    public static string? CheckLegacyJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null
                : $"{LegacyConfig} must hold a JSON object.";
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } l ? $"line {l + 1}" : "an unknown line";
            var column = ex.BytePositionInLine is { } c ? $", column {c + 1}" : string.Empty;
            return $"{LegacyConfig}: {line}{column}: this isn't valid JSON.";
        }
    }
}

/// <summary>The variables that point each Weave at the folder Fleet writes.</summary>
public static class WeaveEnvironment
{
    /// <summary>Weave's global scope folder (it holds <c>config.weave</c> and <c>prompts/</c>).</summary>
    public const string GlobalConfigDir = "WEAVE_GLOBAL_CONFIG_DIR";

    /// <summary>Weave Legacy's user config folder (it holds <c>weave-opencode.jsonc</c>). Legacy 0.9.0 and later.</summary>
    public const string LegacyConfigDir = "WEAVE_OPENCODE_CONFIG_DIR";

    /// <summary>The variable that points <paramref name="flavor"/> at a folder.</summary>
    public static string For(WeaveFlavor flavor) => flavor == WeaveFlavor.Legacy ? LegacyConfigDir : GlobalConfigDir;
}
