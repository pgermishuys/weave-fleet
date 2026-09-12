using System.Globalization;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Services;

/// <summary>A GitHub pull request or issue identified by owner, repository and number.</summary>
public sealed record GitHubLinkReference(string Owner, string Repo, int Number, string ResourceType)
{
    public const string PullRequest = "pull_request";
    public const string Issue = "issue";

    /// <summary>Canonical URL, used as the link's key within a session.</summary>
    public string Url => ResourceType == PullRequest
        ? $"https://github.com/{Owner}/{Repo}/pull/{Number.ToString(CultureInfo.InvariantCulture)}"
        : $"https://github.com/{Owner}/{Repo}/issues/{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary><c>owner/repo#number</c>, shared by the pull request and issue forms of the same number.</summary>
    public string ResourceId => $"{Owner}/{Repo}#{Number.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Finds GitHub pull request and issue references in text: full URLs (bare or inside markdown links)
/// and <c>owner/repo#123</c> shorthand. Shorthand can't tell a pull request from an issue, so it is
/// reported as an issue; the watcher corrects the type when GitHub says otherwise.
/// </summary>
public static partial class GitHubLinkParser
{
    public static bool TryParseUrl(string url, out GitHubLinkReference reference)
    {
        var match = UrlRegex().Match(url);
        if (match.Success && match.Index == 0 && TryCreate(match, out reference))
            return true;

        reference = null!;
        return false;
    }

    /// <param name="text">The text to scan.</param>
    /// <param name="textMayContinue">
    /// True while the text is still streaming: a reference that runs to the very end could still be
    /// growing (<c>/pull/18</c> becoming <c>/pull/187</c>), so it's skipped until the text is complete.
    /// </param>
    public static IReadOnlyList<GitHubLinkReference> Extract(string? text, bool textMayContinue = false)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var found = new Dictionary<string, GitHubLinkReference>(StringComparer.OrdinalIgnoreCase);

        void Scan(Regex regex)
        {
            foreach (Match match in regex.Matches(text))
            {
                if (textMayContinue && match.Index + match.Length == text.Length)
                    continue;
                if (TryCreate(match, out var reference))
                    Add(found, reference);
            }
        }

        if (text.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            Scan(UrlRegex());

        if (text.Contains('#', StringComparison.Ordinal))
            Scan(ShorthandRegex());

        return [.. found.Values];
    }

    private static void Add(Dictionary<string, GitHubLinkReference> found, GitHubLinkReference reference)
    {
        // A /pull/ URL is more specific than shorthand for the same number.
        if (!found.TryGetValue(reference.ResourceId, out var existing)
            || (existing.ResourceType == GitHubLinkReference.Issue && reference.ResourceType == GitHubLinkReference.PullRequest))
        {
            found[reference.ResourceId] = reference;
        }
    }

    private static bool TryCreate(Match match, out GitHubLinkReference reference)
    {
        reference = null!;
        if (!int.TryParse(match.Groups["number"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return false;

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        if (owner.Length == 0 || repo.Length == 0 || repo is "." or "..")
            return false;

        var kind = match.Groups["kind"].Value;
        var resourceType = kind == "pull" ? GitHubLinkReference.PullRequest : GitHubLinkReference.Issue;
        reference = new GitHubLinkReference(owner, repo, number, resourceType);
        return true;
    }

    [GeneratedRegex(@"https?://(?:www\.)?github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)/(?<kind>pull|issues)/(?<number>\d+)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    // owner/repo#123 not preceded by a path or URL character, and not followed by more word characters.
    [GeneratedRegex(@"(?<![\w/.:@-])(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/(?<repo>[A-Za-z0-9._-]+)#(?<number>\d+)(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex ShorthandRegex();
}
