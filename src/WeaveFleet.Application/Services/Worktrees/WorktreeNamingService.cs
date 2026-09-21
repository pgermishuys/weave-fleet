using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services.Worktrees;

/// <summary>
/// The worktree naming templates in force: Fleet's defaults, then the user's settings, then the
/// repository's committed <c>weave.jsonc</c>. The project layer is the point of the feature — one
/// file in the repository and everyone who clones it names branches the same way.
/// </summary>
public sealed partial class WorktreeNamingService(
    IUserPreferenceRepository preferences,
    ILogger<WorktreeNamingService> logger)
{
    /// <summary>The file a repository ships its convention in, read from the repository root.</summary>
    public const string ProjectConfigFileName = "weave.jsonc";

    private const string KeyPrefix = "worktrees.";
    private const string BranchKey = KeyPrefix + "branch";
    private const string RootKey = KeyPrefix + "root";
    private const string FolderKey = KeyPrefix + "folder";
    private const string PrefixKey = KeyPrefix + "prefix";
    private const string CaptureKey = KeyPrefix + "capture";

    private static readonly JsonDocumentOptions _jsoncOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The templates to name a worktree in <paramref name="repositoryPath"/> with, and which layer
    /// each one came from. A null path asks for the user's own settings, with no project layer.
    /// </summary>
    public async Task<ResolvedWorktreeNaming> GetAsync(string? repositoryPath, CancellationToken ct = default)
    {
        var user = await ReadUserAsync().ConfigureAwait(false);
        var project = repositoryPath is null
            ? WorktreeNamingOverride.Empty
            : await ReadProjectAsync(repositoryPath, ct).ConfigureAwait(false);

        var effective = WorktreeNaming.Defaults.Overlay(user).Overlay(project);

        var layers = new Dictionary<string, WorktreeNamingLayer>(StringComparer.Ordinal)
        {
            ["branch"] = LayerOf(user.Branch, project.Branch),
            ["root"] = LayerOf(user.Root, project.Root),
            ["folder"] = LayerOf(user.Folder, project.Folder),
            ["capture"] = LayerOf(user.Capture, project.Capture),
            ["prefix"] = LayerOf(user.Prefix, project.Prefix),
        };

        return new ResolvedWorktreeNaming(effective, layers);
    }

    /// <summary>The user's own layer, as Settings edits it.</summary>
    public async Task<WorktreeNamingOverride> GetUserAsync(CancellationToken ct = default)
    {
        _ = ct;
        return await ReadUserAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the user's layer, refusing templates that can't name a worktree — a bad template is a
    /// validation error here rather than a git failure later.
    /// </summary>
    public async Task<Result<Unit>> SaveUserAsync(WorktreeNamingOverride layer, CancellationToken ct = default)
    {
        _ = ct;

        var problems = WorktreeNameResolver.Validate(WorktreeNaming.Defaults.Overlay(layer));
        if (problems.Count > 0)
            return problems[0].ToError();

        await preferences.SetAsync(BranchKey, layer.Branch ?? string.Empty).ConfigureAwait(false);
        await preferences.SetAsync(RootKey, layer.Root ?? string.Empty).ConfigureAwait(false);
        await preferences.SetAsync(FolderKey, layer.Folder ?? string.Empty).ConfigureAwait(false);
        await preferences.SetAsync(PrefixKey, layer.Prefix ?? string.Empty).ConfigureAwait(false);
        await preferences.SetAsync(CaptureKey, SerializeCapture(layer.Capture)).ConfigureAwait(false);

        return Unit.Value;
    }

    /// <summary>
    /// The facts templates resolve against for a repository. <paramref name="shortId"/> is passed
    /// in so a preview and the worktree it previews can produce the same <c>{shortid}</c>.
    /// </summary>
    public static WorktreeNamingContext BuildContext(string repositoryPath, string shortId) => new(
        RepositoryPath: repositoryPath,
        UserName: Environment.UserName,
        Date: DateOnly.FromDateTime(DateTime.Now),
        ShortId: shortId,
        HomeDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static WorktreeNamingLayer LayerOf(object? user, object? project) => project is not null
        ? WorktreeNamingLayer.Project
        : user is not null ? WorktreeNamingLayer.User : WorktreeNamingLayer.Default;

    private async Task<WorktreeNamingOverride> ReadUserAsync()
    {
        var stored = await preferences.GetAllAsync().ConfigureAwait(false);

        return new WorktreeNamingOverride
        {
            Branch = Value(stored, BranchKey),
            Root = Value(stored, RootKey),
            Folder = Value(stored, FolderKey),
            Prefix = Value(stored, PrefixKey),
            Capture = ParseCapture(Value(stored, CaptureKey)),
        };

        static string? Value(IReadOnlyDictionary<string, string> stored, string key)
            => stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    /// <summary>
    /// The repository's committed layer. Anything wrong with the file is ignored with a warning —
    /// a colleague's typo must not stop you starting a session.
    /// </summary>
    private async Task<WorktreeNamingOverride> ReadProjectAsync(string repositoryPath, CancellationToken ct)
    {
        var path = Path.Combine(repositoryPath, ProjectConfigFileName);
        if (!File.Exists(path))
            return WorktreeNamingOverride.Empty;

        JsonNode? worktrees;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            worktrees = JsonNode.Parse(json, documentOptions: _jsoncOptions)?["worktrees"];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogProjectConfigUnreadable(ex, path);
            return WorktreeNamingOverride.Empty;
        }

        if (worktrees is not JsonObject block)
            return WorktreeNamingOverride.Empty;

        var layer = new WorktreeNamingOverride
        {
            Branch = Text(block, "branch"),
            Root = Text(block, "root"),
            Folder = Text(block, "folder"),
            Prefix = Text(block, "prefix"),
            Capture = CaptureFrom(block),
        };

        var problems = WorktreeNameResolver.Validate(WorktreeNaming.Defaults.Overlay(layer));
        if (problems.Count == 0)
            return layer;

        LogProjectConfigRejected(path, problems[0].Field, problems[0].Message);
        return WorktreeNamingOverride.Empty;

        static string? Text(JsonObject block, string name)
            => block[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;

        static IReadOnlyDictionary<string, string>? CaptureFrom(JsonObject block)
        {
            if (block["capture"] is not JsonObject captures)
                return null;

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, node) in captures)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var pattern) && pattern.Length > 0)
                    parsed[name] = pattern;
            }

            return parsed.Count > 0 ? parsed : null;
        }
    }

    private static string SerializeCapture(IReadOnlyDictionary<string, string>? capture)
    {
        if (capture is null || capture.Count == 0)
            return string.Empty;

        var node = new JsonObject();
        foreach (var (name, pattern) in capture)
            node[name] = pattern;

        return node.ToJsonString();
    }

    private static Dictionary<string, string>? ParseCapture(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        try
        {
            if (JsonNode.Parse(stored) is not JsonObject node)
                return null;

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in node)
            {
                if (value is JsonValue text && text.TryGetValue<string>(out var pattern) && pattern.Length > 0)
                    parsed[name] = pattern;
            }

            return parsed.Count > 0 ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read worktree naming from {Path}; using your own settings")]
    private partial void LogProjectConfigUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring worktree naming in {Path}: {Field} {Problem}")]
    private partial void LogProjectConfigRejected(string path, string field, string problem);
}
