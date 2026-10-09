using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Walkthroughs;

/// <summary>
/// The agent's <c>fleet_walkthrough_show</c>: a change as a guided walkthrough, in a page canvas. The agent sends the
/// outline (<see cref="WalkthroughGuide"/>); Fleet takes the change the way the Changes tab does (or a range the guide
/// names), checks that every file the guide names is in it, puts each file's hunks under its chapter, and shows the
/// page (<see cref="PageBridge"/>). The same title updates the same tab.
/// </summary>
public sealed partial class WalkthroughBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ISessionRepository sessions,
    GitDiffService git,
    PageBridge pages)
{
    /// <summary>Set to <c>1</c> in an OpenCode process's environment when it was started with fleet-walkthrough on: its plugin then has the tool.</summary>
    public const string EnvironmentVariable = "FLEET_WALKTHROUGH";

    internal const string TemplateResource = "walkthrough.html";
    private const string DataSlot = "<script id=\"walkthrough-data\" type=\"application/json\">{}</script>";

    /// <summary>Past this, files are listed without their hunks, so the page stays a size a browser opens at once.</summary>
    private const int MaxPatchBytes = 12 * 1024 * 1024;

    private static readonly Lazy<string> Template = new(ReadTemplate);

    public async Task<CanvasResult<CanvasToolOutput>> ShowAsync(
        string? bridgeToken, string? harnessSessionId, string? title, JsonElement guide, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct);
        if (caller is null)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
            return await ShowForSessionAsync(caller.FleetSessionId, title, guide, ct);
    }

    internal async Task<CanvasResult<CanvasToolOutput>> ShowForSessionAsync(string sessionId, string? title, JsonElement guideElement, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Invalid("\"title\" is required: what the change does, e.g. \"Branches compare with where they left main\".");

        // A model may send the guide as JSON in a string.
        if (guideElement.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(guideElement.GetString()!);
                guideElement = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return Invalid($"\"guide\" is a string that isn't JSON ({ex.Message}). Send it as an object.");
            }
        }

        var (guide, errors) = WalkthroughGuide.Parse(guideElement);
        if (guide is null)
            return Invalid("The guide isn't ready:\n- " + string.Join("\n- ", errors) + "\nFix these and call fleet_walkthrough_show again.");

        var session = await sessions.GetByIdAsync(sessionId);
        if (session is null)
            return Invalid("Fleet couldn't find this session.");

        var scope = await SessionDiffScope.ResolveAsync(session, git, ct);
        if (scope is null)
            return Invalid("This session's folder isn't in a git repository with a change to compare, so there's nothing to walk through.");

        var change = await ResolveChangeAsync(scope, guide, ct);
        if (change.Problem is not null)
            return Invalid(change.Problem);

        var diff = await git.ComputeDiffsWithAvailabilityAsync(scope.RepoRoot, change.Diff!, scope.WorkspacePrefix, ct, change.WithUntracked);
        if (!diff.Available)
            return Invalid($"Fleet couldn't read the change ({change.Label}).");
        if (diff.Diffs.Count == 0)
            return Invalid($"There are no changes to walk through ({change.Label}).");

        var files = diff.Diffs.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        var byPath = files.ToDictionary(file => file.Path, StringComparer.Ordinal);

        // Paths the agent gave, matched to the change: as git names them (from the repository's root), or from the
        // session's folder when that's a folder within it.
        var unknown = new List<string>();
        var chapters = new JsonArray();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in guide.Chapters)
        {
            var chapterFiles = new JsonArray();
            foreach (var file in chapter.Files)
            {
                if (Match(file.Path, scope.WorkspacePrefix, byPath) is not { } path)
                {
                    unknown.Add(file.Path);
                    continue;
                }

                covered.Add(path);
                chapterFiles.Add((JsonNode)new JsonObject { ["path"] = path, ["collapsed"] = file.Collapsed });
            }

            chapters.Add((JsonNode)new JsonObject
            {
                ["title"] = chapter.Title,
                ["body"] = Strings(chapter.Body),
                ["cite"] = chapter.Cite,
                ["files"] = chapterFiles,
                ["closer"] = Strings(chapter.Closer),
            });
        }

        if (unknown.Count > 0)
        {
            var listed = string.Join(", ", files.Take(40).Select(file => file.Path)) + (files.Count > 40 ? $", and {files.Count - 40} more" : string.Empty);
            return Invalid($"Not in the change ({change.Label}): {string.Join(", ", unknown.Distinct())}. Name files as git does, from the repository's root. The change has: {listed}.");
        }

        var also = new JsonArray();
        var unmatched = new List<string>();
        foreach (var item in guide.AlsoChanged)
        {
            var matched = files.Where(file => Glob(item.Path, scope.WorkspacePrefix).IsMatch(file.Path)).Select(file => file.Path).ToList();
            if (matched.Count == 0)
                unmatched.Add(item.Path);
            covered.UnionWith(matched);
            also.Add((JsonNode)new JsonObject { ["path"] = item.Path, ["note"] = item.Note });
        }

        var uncovered = files.Select(file => file.Path).Where(path => !covered.Contains(path)).ToList();
        var page = new JsonObject
        {
            ["title"] = title.Trim(),
            ["comparedWith"] = change.Label,
            ["guide"] = new JsonObject
            {
                ["summary"] = guide.Summary,
                ["steps"] = new JsonArray([.. guide.Steps.Select(step => (JsonNode)new JsonObject { ["text"] = step.Text, ["chapter"] = step.Chapter })]),
                ["diagram"] = guide.Diagram,
                ["chapters"] = chapters,
                ["alsoChanged"] = also,
            },
            ["files"] = await FilesAsync(scope.RepoRoot, change.Diff!, files, ct),
            ["uncovered"] = Strings(uncovered),
        };

        var folder = Directory.CreateTempSubdirectory("fleet-walkthrough-").FullName;
        try
        {
            var entry = Path.Combine(folder, "index.html");
            await File.WriteAllTextAsync(entry, Html(title.Trim(), page), ct);

            var published = await pages.PublishAsync(sessionId, Source(title), entry, title.Trim(), [], ct, $"Walkthrough · {change.Label}");
            if (!published.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(published.Error);

            var (canvas, copy, updated, check) = published.Value;
            var output = new StringBuilder()
                .Append(updated ? "Updated " : "Showing ").Append(CanvasText.CanvasName(canvas)).Append(": ")
                .Append(guide.Chapters.Count).Append(guide.Chapters.Count == 1 ? " chapter" : " chapters").Append(" over ")
                .Append(files.Count - uncovered.Count).Append(" of the ").Append(files.Count).Append(" changed files (").Append(change.Label).Append(").");
            if (uncovered.Count > 0)
                output.Append("\nNot in any chapter or alsoChanged, so listed under \"Not in the walkthrough\": ").Append(string.Join(", ", uncovered.Take(20)))
                    .Append(uncovered.Count > 20 ? $", and {uncovered.Count - 20} more" : string.Empty).Append('.');
            if (unmatched.Count > 0)
                output.Append("\nalsoChanged matched no changed file: ").Append(string.Join(", ", unmatched)).Append('.');
            output.Append("\nCall fleet_walkthrough_show again with the same title to update this tab. The user can mark chapters reviewed, and \"Ask about this\" puts a question about a chapter in their composer.");
            output.Append('\n').Append(check);
            return CanvasResult.Ok(new CanvasToolOutput(
                $"{canvas.Title} · {guide.Chapters.Count} {(guide.Chapters.Count == 1 ? "chapter" : "chapters")}", output.ToString(), canvas.Id, canvas.Version));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder left behind is harmless.
            }
        }
    }

    /// <summary>What the walkthrough compares, as git takes it, and how the page names it.</summary>
    private sealed record Change(string? Diff, bool WithUntracked, string Label, string? Problem = null);

    /// <summary>
    /// The session's changes as the Changes tab shows them; or, with <c>from</c>, the folder as it is against where it
    /// left <c>from</c>; or, with both, what <c>to</c> adds to <c>from</c> (<c>from...to</c>, as a pull request shows it).
    /// </summary>
    private async Task<Change> ResolveChangeAsync(SessionDiffScope scope, WalkthroughGuide guide, CancellationToken ct)
    {
        if (guide.From is null)
        {
            var label = scope.Base.Kind == GitDiffBaseKind.Branch
                ? $"compared with {scope.Base.MainBranch ?? "main"} at {Short(scope.Base.Ref)}"
                : "since this session started";
            return new Change(scope.Base.Ref, WithUntracked: true, label);
        }

        if (await git.ResolveCommitAsync(scope.RepoRoot, guide.From, ct) is not { } from)
            return new Change(null, false, string.Empty, $"guide.from \"{guide.From}\" isn't a branch, tag or commit in this repository. For a pull request, fetch it first (git fetch origin pull/<n>/head) and pass its base branch and the fetched commit.");

        if (guide.To is null)
        {
            var head = await git.ResolveCommitAsync(scope.RepoRoot, "HEAD", ct);
            var mergeBase = head is null ? null : await git.MergeBaseAsync(scope.RepoRoot, from, head, ct);
            return mergeBase is null
                ? new Change(null, false, string.Empty, $"This folder's branch has nothing in common with {guide.From}.")
                : new Change(mergeBase, WithUntracked: true, $"compared with {guide.From} at {Short(mergeBase)}");
        }

        if (await git.ResolveCommitAsync(scope.RepoRoot, guide.To, ct) is not { } to)
            return new Change(null, false, string.Empty, $"guide.to \"{guide.To}\" isn't a branch, tag or commit in this repository.");

        return new Change($"{from}...{to}", WithUntracked: false, $"{guide.To} against {guide.From}");
    }

    private async Task<JsonArray> FilesAsync(string repoRoot, string diff, List<FileDiffSummary> files, CancellationToken ct)
    {
        var patches = new FilePatch[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 },
            async (index, token) => patches[index] = await git.ReadPatchAsync(repoRoot, diff, files[index], token));

        var array = new JsonArray();
        long bytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var patch = patches[i];
            var text = patch.Text;
            var tooBig = patch.TooBig;
            if (text is not null && (bytes += Encoding.UTF8.GetByteCount(text)) > MaxPatchBytes)
            {
                text = null;
                tooBig = true;
            }

            array.Add((JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["status"] = file.Status ?? (file.IsUntracked ? "added" : "modified"),
                ["additions"] = file.AddedLines ?? 0,
                ["deletions"] = file.DeletedLines ?? 0,
                ["binary"] = file.IsBinary || patch.Binary,
                ["tooBig"] = tooBig,
                ["patch"] = text,
            });
        }

        return array;
    }

    /// <summary>The page: Fleet's template with the walkthrough in its data slot. The JSON escapes &lt; and &gt;, so it can't end the script.</summary>
    internal static string Html(string title, JsonObject page)
        => Template.Value
            .Replace("<title>Walkthrough</title>", $"<title>{WebUtility.HtmlEncode(title)}</title>", StringComparison.Ordinal)
            .Replace(DataSlot, DataSlot.Replace("{}", page.ToJsonString(), StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>The page tab's source: one tab per title.</summary>
    internal static string Source(string title) => "walkthrough:" + title.Trim();

    private static string? Match(string path, string prefix, Dictionary<string, FileDiffSummary> byPath)
    {
        var clean = Normalize(path);
        if (byPath.ContainsKey(clean))
            return clean;
        var within = prefix.Length > 0 ? $"{prefix}/{clean}" : null;
        return within is not null && byPath.ContainsKey(within) ? within : null;
    }

    /// <summary><c>*</c> within a folder, <c>**</c> across folders; a pattern from the session's folder matches too.</summary>
    internal static Regex Glob(string pattern, string prefix)
    {
        static string ToRegex(string glob) => Regex.Escape(glob)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);

        var clean = Normalize(pattern);
        var alternatives = prefix.Length > 0 ? $"{ToRegex(clean)}|{ToRegex($"{prefix}/{clean}")}" : ToRegex(clean);
        return new Regex($"^(?:{alternatives})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string Normalize(string path)
    {
        var clean = path.Replace('\\', '/').Trim();
        while (clean.StartsWith("./", StringComparison.Ordinal))
            clean = clean[2..];
        return clean.TrimStart('/');
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)JsonValue.Create(value))]);

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    private static string ReadTemplate()
    {
        using var stream = typeof(WalkthroughBridge).Assembly.GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException($"{TemplateResource} isn't embedded in {typeof(WalkthroughBridge).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static CanvasResult<CanvasToolOutput> Invalid(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, message);

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}
