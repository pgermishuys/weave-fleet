using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Services.Worktrees;

/// <summary>
/// Resolves the worktree naming templates. Pure: the same inputs always give the same names, so
/// the composer's preview and the worktree it previews agree.
/// </summary>
public static partial class WorktreeNameResolver
{
    /// <summary>Tokens every field can use.</summary>
    private static readonly string[] _commonTokens = ["repo", "user", "prefix", "date", "shortid"];

    /// <summary>Tokens each field can use on top of <see cref="_commonTokens"/> and the captures.</summary>
    private static readonly string[] _branchOnlyTokens = ["slug"];
    private static readonly string[] _rootOnlyTokens = ["repoParent", "home"];
    private static readonly string[] _folderOnlyTokens = ["branch", "slug"];

    /// <summary>
    /// A capture can't be read without a message, and the root has to resolve before there is one
    /// (the allowlist needs it), so the root's tokens are the ones that don't depend on the message.
    /// </summary>
    private static readonly HashSet<string> _messageFreeFields = new(StringComparer.Ordinal) { "root" };

    private const int MaxRegexLength = 200;
    private static readonly TimeSpan _captureTimeout = TimeSpan.FromMilliseconds(100);

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Names a worktree for <paramref name="message"/>. A branch of null means the template wanted
    /// a slug the message couldn't give, and the caller should fall back to its own name — without
    /// it, "{prefix}/{slug}" would collapse to the prefix alone and collide with the next one.
    /// </summary>
    /// <param name="branchOverride">
    /// A branch the caller has already settled — a typed override, a source's suggestion, or the
    /// fallback name for a message that gave no slug. The folder then resolves against it, so
    /// <c>{branch}</c> still names the folder after the branch the worktree actually gets.
    /// </param>
    public static WorktreeNameResult Resolve(
        WorktreeNaming naming,
        WorktreeNamingContext context,
        string? message,
        string? branchOverride = null)
    {
        var captures = ReadCaptures(naming.Capture, message);

        // What a capture consumed is no longer part of the slug, or "{ticket}-{slug}" says it
        // twice: "feature/PLAT-1841-plat-1841-add-rate-limiting".
        var slugSource = message ?? string.Empty;
        foreach (var captured in captures.Values.Where(value => value.Length > 0))
            slugSource = slugSource.Replace(captured, " ", StringComparison.Ordinal);

        var slug = BranchSlug.From(slugSource);
        var values = TokenValues(naming, context, captures, slug);

        string? branch;
        if (branchOverride is { Length: > 0 })
        {
            branch = branchOverride;
        }
        else
        {
            // A template built around {slug} with nothing to slug would collapse to its prefix
            // alone ("pg"), which git accepts and which collides with the next one.
            branch = naming.Branch.Contains("{slug}", StringComparison.Ordinal) && slug.Length == 0
                ? null
                : Collapse(Substitute(naming.Branch, values));

            if (branch is { Length: 0 })
                branch = null;
        }

        var root = CollapsePath(Substitute(naming.Root, RootTokenValues(naming, context)));

        var folderValues = new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["branch"] = branch ?? string.Empty,
        };
        var folder = Collapse(Substitute(naming.Folder, folderValues))
            .Replace('/', '-')
            .Replace('\\', '-');

        return new WorktreeNameResult(branch, root, folder);
    }

    /// <summary>
    /// The worktree root for a repository, which needs no message — the allowlist and cleanup both
    /// ask for it before any session exists.
    /// </summary>
    public static string ResolveRoot(WorktreeNaming naming, WorktreeNamingContext context)
        => CollapsePath(Substitute(naming.Root, RootTokenValues(naming, context)));

    /// <summary>
    /// The fixed part of the root template — everything before the first segment that varies per
    /// repository. <c>{home}/worktrees/{repo}</c> gives <c>&lt;home&gt;/worktrees</c>, so the
    /// allowlist can admit worktrees kept outside the repository's parent without admitting
    /// anywhere else. Null when the root starts inside the repository's own folder, which the
    /// allowlist already covers.
    /// </summary>
    public static string? ResolveRootPrefix(WorktreeNaming naming, WorktreeNamingContext context)
    {
        var template = naming.Root.Replace('\\', '/');
        var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var fixedSegments = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.Contains("{repo}", StringComparison.Ordinal)
                || segment.Contains("{repoParent}", StringComparison.Ordinal))
            {
                break;
            }

            fixedSegments.Add(segment);
        }

        if (fixedSegments.Count == 0)
            return null;

        var prefix = CollapsePath(Substitute(
            string.Join('/', fixedSegments), RootTokenValues(naming, context)));

        // A template whose fixed part is a bare relative name isn't a place; only an absolute
        // prefix is worth admitting.
        if (prefix.Length == 0)
            return null;

        var rooted = template.StartsWith('/') ? "/" + prefix : prefix;
        return Path.IsPathRooted(rooted) ? rooted : null;
    }

    /// <summary>
    /// Everything wrong with these templates, so Settings can refuse them instead of git failing
    /// at <c>worktree add</c>. Empty when they're usable.
    /// </summary>
    public static IReadOnlyList<WorktreeNamingProblem> Validate(WorktreeNaming naming)
    {
        var problems = new List<WorktreeNamingProblem>();
        var captureNames = naming.Capture.Keys.ToArray();

        foreach (var (name, pattern) in naming.Capture)
        {
            if (pattern.Length > MaxRegexLength)
            {
                problems.Add(new WorktreeNamingProblem(
                    $"Capture.{name}", $"The pattern for {{{name}}} is longer than {MaxRegexLength} characters."));
                continue;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, _captureTimeout);
            }
            catch (ArgumentException ex)
            {
                problems.Add(new WorktreeNamingProblem($"Capture.{name}", $"That isn't a valid pattern: {ex.Message}"));
            }
        }

        // An empty prefix with a template built around it would name every worktree the same way
        // the empty-slug case would, so it's refused where it's set rather than at git.
        if (naming.Branch.Contains("{prefix}", StringComparison.Ordinal) && naming.Prefix.Length == 0)
            problems.Add(new WorktreeNamingProblem("prefix", "This can't be empty while the branch name uses {prefix}."));

        problems.AddRange(ValidateField("branch", naming.Branch, _branchOnlyTokens, captureNames));
        problems.AddRange(ValidateField("root", naming.Root, _rootOnlyTokens, captureNames));
        problems.AddRange(ValidateField("folder", naming.Folder, _folderOnlyTokens, captureNames));

        if (problems.Count > 0)
            return problems;

        // A template can be well-formed and still name something git refuses, so try it on a
        // message that exercises every token.
        var sample = Resolve(naming, SampleContext, "PLAT-1 add rate limiting to the endpoint");

        if (sample.Branch is null)
        {
            problems.Add(new WorktreeNamingProblem("branch", "That template doesn't name anything."));
        }
        else if (!WorkspaceService.IsValidBranchName(sample.Branch))
        {
            problems.Add(new WorktreeNamingProblem(
                "branch", $"That template resolves to “{sample.Branch}”, which git won't take as a branch name."));
        }

        if (sample.Folder.Length == 0)
            problems.Add(new WorktreeNamingProblem("folder", "That template doesn't name anything."));
        else if (sample.Folder is "." or ".." || sample.Folder.Contains("..", StringComparison.Ordinal))
            problems.Add(new WorktreeNamingProblem("folder", $"“{sample.Folder}” isn't a folder name."));

        if (sample.Root.Length == 0)
            problems.Add(new WorktreeNamingProblem("root", "That template doesn't name anywhere."));

        return problems;
    }

    private static readonly WorktreeNamingContext SampleContext = new(
        RepositoryPath: Path.Combine("home", "you", "source", "repo"),
        UserName: "you",
        Date: new DateOnly(2026, 1, 1),
        ShortId: "0a1b2c3d",
        HomeDirectory: Path.Combine("home", "you"));

    private static IEnumerable<WorktreeNamingProblem> ValidateField(
        string field,
        string template,
        IReadOnlyList<string> fieldTokens,
        IReadOnlyList<string> captureNames)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            yield return new WorktreeNamingProblem(field, "This can't be empty.");
            yield break;
        }

        var allowed = new HashSet<string>(_commonTokens, StringComparer.Ordinal);
        allowed.UnionWith(fieldTokens);
        if (!_messageFreeFields.Contains(field))
            allowed.UnionWith(captureNames);

        foreach (var match in TokenPattern().Matches(template).Cast<Match>())
        {
            var token = match.Groups[1].Value;
            if (allowed.Contains(token))
                continue;

            yield return captureNames.Contains(token, StringComparer.Ordinal)
                ? new WorktreeNamingProblem(field, $"{{{token}}} is read from the message, which isn't known here.")
                : new WorktreeNamingProblem(field, $"There's no {{{token}}} token. Try {string.Join(", ", allowed.Order(StringComparer.Ordinal).Select(name => $"{{{name}}}"))}.");
        }
    }

    private static Dictionary<string, string> TokenValues(
        WorktreeNaming naming,
        WorktreeNamingContext context,
        IReadOnlyDictionary<string, string> captures,
        string slug)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slug"] = slug,
            ["repo"] = RepositoryName(context.RepositoryPath),
            ["user"] = context.UserName,
            ["prefix"] = naming.Prefix,
            ["date"] = context.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["shortid"] = context.ShortId,
        };

        foreach (var (name, value) in captures)
            values[name] = value;

        return values;
    }

    private static Dictionary<string, string> RootTokenValues(WorktreeNaming naming, WorktreeNamingContext context)
    {
        var trimmed = context.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repo"] = RepositoryName(context.RepositoryPath),
            ["repoParent"] = Path.GetDirectoryName(trimmed) ?? trimmed,
            ["home"] = context.HomeDirectory,
            ["user"] = context.UserName,
            ["prefix"] = naming.Prefix,
            ["date"] = context.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["shortid"] = context.ShortId,
        };
    }

    private static string RepositoryName(string repositoryPath)
    {
        var trimmed = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static Dictionary<string, string> ReadCaptures(
        IReadOnlyDictionary<string, string> patterns,
        string? message)
    {
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (patterns.Count == 0)
            return captures;

        foreach (var (name, pattern) in patterns)
        {
            captures[name] = string.Empty;
            if (string.IsNullOrEmpty(message) || pattern.Length is 0 or > MaxRegexLength)
                continue;

            try
            {
                var match = Regex.Match(message, pattern, RegexOptions.CultureInvariant, _captureTimeout);
                if (match.Success)
                {
                    // A group means "this part", no groups means the whole match.
                    captures[name] = match.Groups.Count > 1 && match.Groups[1].Success
                        ? match.Groups[1].Value
                        : match.Value;
                }
            }
            catch (ArgumentException)
            {
                // A pattern Settings would have refused; naming carries on without it.
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return captures;
    }

    private static string Substitute(string template, Dictionary<string, string> values)
        => TokenPattern().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : string.Empty);

    /// <summary>
    /// Tidies what an empty token left behind: doubled or dangling separators. Without this,
    /// "{user}/{ticket}-{slug}" with no ticket gives "pg/-add-rate-limiting".
    /// </summary>
    private static string Collapse(string value)
        => Tidy(value).Trim('-', '_', '/', '.', ' ');

    /// <summary>
    /// The same tidying for a path, which keeps its leading separator — trimming it would turn an
    /// absolute root into a relative one.
    /// </summary>
    private static string CollapsePath(string value)
        => Tidy(value).TrimEnd('-', '_', '/', '.', ' ');

    private static string Tidy(string value)
        => SeparatorBesideSlash().Replace(SeparatorRuns().Replace(value, "$1"), "/");

    [GeneratedRegex(@"([-_/])\1+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRuns();

    [GeneratedRegex(@"[-_]?/[-_]?", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorBesideSlash();
}
