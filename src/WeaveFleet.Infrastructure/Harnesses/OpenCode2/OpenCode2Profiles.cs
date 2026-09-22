using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// A profile as an OpenCode 2 server runs it: its content in a file named by the content's hash, which the server gets
/// as <c>OPENCODE_CONFIG</c>. Each version has its own file and its own server, so a session keeps the version it
/// started with while new sessions get an edit.
/// </summary>
internal sealed record OpenCode2Profile(string Hash, string ConfigPath);

/// <summary>
/// How Fleet hands a profile to OpenCode 2, and how it checks one before it's saved.
/// </summary>
/// <remarks>
/// V2 reads <c>OPENCODE_CONFIG</c> as V1 does, and layers it the same way (checked live on 2.0.6 and 2.0.9, see
/// <c>GET /api/config</c>): the user's config folder, then the profile, then the folder's own <c>opencode.json</c>,
/// then <c>OPENCODE_CONFIG_CONTENT</c> with Fleet's plugin and skills. Arrays (<c>plugins</c>, <c>skills</c>) add up
/// across the layers, so the profile and Fleet's config never need merging, and a profile's own plugins and skills
/// load next to Fleet's. JSONC (comments, trailing commas) and V1 syntax both work.
/// <para>
/// V2 never refuses a config. It starts, answers every request, and loads a folder with whatever of the config it could
/// use; what it dropped it only writes to its log, as a <c>configuration normalization diagnostic</c>, when a folder
/// loads. Settings it doesn't know it drops without a word, and a <c>model</c> it has no provider for falls back to
/// another model. So the check (<see cref="Judge"/>) reads the log, the profile as V2 stored it, and the model a
/// session would get.
/// </para>
/// </remarks>
internal static partial class OpenCode2Profiles
{
    /// <summary>The variable V2 reads a config file's path from.</summary>
    public const string EnvironmentVariable = "OPENCODE_CONFIG";

    /// <summary>
    /// The top-level settings of V2's own config (its <c>Config.Info</c>, 2.0.9), and the V1 settings it reads or
    /// ignores on purpose (<c>migrate-v1</c>). Anything else V2 drops without saying so.
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema", "agents", "commands", "compaction", "default_agent", "enterprise", "experimental", "formatter",
        "instructions", "lsp", "mcp", "media", "model", "permissions", "plugins", "providers", "references", "share",
        "shell", "skills", "snapshots", "tool_output", "update", "username", "warming", "watcher", "websearch",
        "worktree",
        // V1
        "agent", "mode", "command", "plugin", "permission", "tools", "provider", "snapshot", "autoshare",
        "autoupdate", "small_model", "enabled_providers", "disabled_providers", "attachments", "logLevel", "server",
        "subagent_depth", "theme", "keybinds", "tui", "layout",
    };

    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Writes <paramref name="content"/> to <c>{dataDirectory}/opencode2/profiles/profile-{hash}.json</c> unless it's
    /// there, and returns it.
    /// </summary>
    public static OpenCode2Profile Write(string dataDirectory, string content)
    {
        var hash = Hash(content);
        var directory = Path.Combine(dataDirectory, "opencode2", "profiles");
        var path = Path.Combine(directory, $"profile-{hash}.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }

        return new OpenCode2Profile(hash, path);
    }

    internal static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();

    /// <summary>
    /// The profile's settings, or why it can't be read. V2 would take a document that isn't a JSON object as no config
    /// at all, and say only that; this says where it breaks.
    /// </summary>
    public static HarnessProfileCheck? ReadSettings(string content, out JsonDocument? settings)
    {
        settings = null;
        try
        {
            var document = JsonDocument.Parse(content, JsoncOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return new HarnessProfileCheck(false, "This profile isn't a JSON object.",
                    ["A profile is an opencode.json: settings between { and }."]);
            }

            settings = document;
            return null;
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $"Line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}: " : string.Empty;
            return new HarnessProfileCheck(false, "This profile isn't valid JSON.", [$"{where}{FirstSentence(ex.Message)}"]);
        }
    }

    /// <summary>What V2 wrote to its log about one config document or plugin, read from a <c>--print-logs</c> line.</summary>
    internal sealed record LogDiagnostic(string Message, IReadOnlyDictionary<string, string> Fields);

    /// <summary>
    /// A V2 log line (<c>key=value key="quoted value" …</c>) that says something about a config document or a plugin, or
    /// <see langword="null"/> for any other line.
    /// </summary>
    public static LogDiagnostic? ParseLogLine(string line)
    {
        if (!line.Contains("configuration normalization diagnostic", StringComparison.Ordinal)
            && !line.Contains("failed to load plugin", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in LogFieldPattern().Matches(line))
        {
            var value = match.Groups["quoted"].Success
                ? Regex.Unescape(match.Groups["quoted"].Value)
                : match.Groups["bare"].Value;
            fields[match.Groups["key"].Value] = value;
        }

        return fields.TryGetValue("message", out var message) ? new LogDiagnostic(message, fields) : null;
    }

    [GeneratedRegex("""(?<key>[A-Za-z_.]+)=(?:"(?<quoted>(?:[^"\\]|\\.)*)"|(?<bare>\S*))""")]
    private static partial Regex LogFieldPattern();

    /// <summary>
    /// Whether V2 would use all of the profile at <paramref name="profilePath"/>: nothing in it skipped, dropped or
    /// failing to load, and its <c>model</c> the one a session gets.
    /// </summary>
    /// <param name="settings">The profile's settings (<see cref="ReadSettings"/>).</param>
    /// <param name="config">V2's <c>GET /api/config</c> for a folder with no config of its own.</param>
    /// <param name="defaultModel">The model a new session in that folder would get.</param>
    /// <param name="log">What V2 logged while it loaded that folder.</param>
    public static HarnessProfileCheck Judge(
        string profilePath,
        JsonElement settings,
        IReadOnlyList<OpenCode2ConfigSource> config,
        OpenCode2ModelInfo? defaultModel,
        IReadOnlyList<LogDiagnostic> log)
    {
        var problems = new List<string>();
        var stored = config.FirstOrDefault(source => source.Type == "document" && SamePath(source.Path, profilePath));

        var diagnostics = log
            .Where(entry => entry.Message == "configuration normalization diagnostic"
                && entry.Fields.TryGetValue("source", out var source) && SamePath(source, profilePath))
            .ToList();
        foreach (var diagnostic in diagnostics.DistinctBy(d => (Field(d, "path"), Field(d, "kind"))))
            problems.Add(Describe(diagnostic));

        if (stored is null)
        {
            return new HarnessProfileCheck(false, "OpenCode 2 couldn't read this profile.",
                problems.Count > 0 ? problems : null);
        }

        // Settings V2 doesn't know leave no trace: they're neither in what it stored nor in its log.
        var storedKeys = stored.Info.ValueKind == JsonValueKind.Object
            ? stored.Info.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        var reported = diagnostics.Select(d => TopLevel(Field(d, "path"))).ToHashSet(StringComparer.Ordinal);
        foreach (var setting in settings.EnumerateObject().Select(p => p.Name))
        {
            if (!KnownSettings.Contains(setting) && !storedKeys.Contains(setting) && !reported.Contains(setting))
                problems.Add($"{setting}: OpenCode 2 doesn't know this setting and ignores it.");
        }

        foreach (var target in PluginEntries(settings))
        {
            var failed = log.FirstOrDefault(entry => entry.Message == "failed to load plugin"
                && entry.Fields.TryGetValue("target", out var loaded) && SamePlugin(loaded, target));
            if (failed is not null)
                problems.Add($"plugin {target}: OpenCode 2 couldn't load it{Cause(failed)}.");
        }

        if (StoredModel(stored.Info) is { } model
            && (defaultModel is null || defaultModel.ProviderId != model.ProviderId || defaultModel.Id != model.ModelId))
        {
            var instead = defaultModel is { ProviderId: { } provider, Id: { } id } ? $", so sessions would use {provider}/{id} instead" : string.Empty;
            problems.Add($"model: OpenCode 2 has no model {model.ProviderId}/{model.ModelId}{instead}. Check the provider is set up.");
        }

        return problems.Count == 0
            ? HarnessProfileCheck.Passed
            : new HarnessProfileCheck(false, "OpenCode 2 would leave out part of this profile.", problems);
    }

    private static string Describe(LogDiagnostic diagnostic)
    {
        var path = Field(diagnostic, "path");
        var where = path is "$" or "" ? "The profile" : path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
        return Field(diagnostic, "kind") switch
        {
            "invalid" when path is "$" or "" => "The profile: OpenCode 2 couldn't read it as JSON.",
            "invalid" => $"{where}: OpenCode 2 skipped this value because it isn't valid.",
            "unsupported" => $"{where}: OpenCode 2 doesn't support this setting and ignores it.",
            _ => $"{where}: OpenCode 2 {Field(diagnostic, "action")}.".Replace(" .", ".", StringComparison.Ordinal),
        };
    }

    private static string Field(LogDiagnostic diagnostic, string name)
        => diagnostic.Fields.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>The setting a diagnostic's JSON path (<c>$.agent.bad</c>) is under (<c>agent</c>).</summary>
    private static string TopLevel(string path)
        => path.StartsWith("$.", StringComparison.Ordinal) ? path[2..].Split('.', '[')[0] : string.Empty;

    /// <summary>V2's own words for why a plugin didn't load: <c>Cause([Die(Error: ENOENT: no such file …)])</c>.</summary>
    private static string Cause(LogDiagnostic failed)
        => Field(failed, "cause") is { Length: > 0 } cause && CausePattern().Match(cause) is { Success: true } match
            ? $" ({match.Groups["why"].Value.Trim()})"
            : string.Empty;

    [GeneratedRegex(@"Error: (?<why>[^\)\]]+)")]
    private static partial Regex CausePattern();

    private static IEnumerable<string> PluginEntries(JsonElement settings)
    {
        foreach (var key in (string[])["plugin", "plugins"])
        {
            if (!settings.TryGetProperty(key, out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind == JsonValueKind.String && plugin.GetString() is { Length: > 0 } name)
                    yield return name;
                else if (plugin.ValueKind == JsonValueKind.Array && plugin.GetArrayLength() > 0
                         && plugin[0].ValueKind == JsonValueKind.String && plugin[0].GetString() is { Length: > 0 } tupleName)
                    yield return tupleName;
            }
        }
    }

    private static bool SamePlugin(string loaded, string configured)
    {
        static string Bare(string name) => name.StartsWith("file://", StringComparison.Ordinal) ? name["file://".Length..] : name;
        var (a, b) = (Bare(loaded), Bare(configured));
        return a == b || a.EndsWith(b, StringComparison.Ordinal) || b.EndsWith(a, StringComparison.Ordinal);
    }

    /// <summary>The model the stored profile sets, in V2's shape (<c>{"providerID", "model"}</c>).</summary>
    private static (string ProviderId, string ModelId)? StoredModel(JsonElement info)
        => info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("providerID", out var provider) && provider.GetString() is { Length: > 0 } providerId
            && model.TryGetProperty("model", out var id) && id.GetString() is { Length: > 0 } modelId
                ? (providerId, modelId)
                : null;

    private static bool SamePath(string? a, string b)
        => a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (end > 0 ? message[..end] : message).Trim();
    }
}
