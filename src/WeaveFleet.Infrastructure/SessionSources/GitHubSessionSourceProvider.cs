using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.SessionSources;

public sealed class GitHubSessionSourceProvider(
    GitHubService gitHubService,
    GitHubApiProxy gitHubApiProxy) : ISessionSourceProvider
{
    private const int MaxContextCharacters = 12000;
    private static readonly string[] SecretIndicators =
    [
        "token",
        "password",
        "secret",
        "api_key",
        "apikey",
        "private_key",
        "authorization:",
        "bearer ",
        "ghp_",
        "github_pat_",
        "glpat-",
        "sk-"
    ];
    private static readonly char[] SecretDelimiters = [' ', '\t', ':', '=', '"', '\'', '`'];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string ProviderId => SessionSourceProviderIds.GitHub;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() =>
    [
        BuildDescriptor(SessionSourceTypeNames.GitHubIssue),
        BuildDescriptor(SessionSourceTypeNames.GitHubPullRequest)
    ];

    public async Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (selection.Input.ValueKind != JsonValueKind.Object)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                "Session source input must be a JSON object.");
        }

        if (!string.Equals(selection.Key.ActionId, SessionSourceActions.AddToSession, StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "SessionSource.ActionId",
                "GitHub session sources currently support only add-to-session.");
        }

        if (selection.Key.SourceType is not (SessionSourceTypeNames.GitHubIssue or SessionSourceTypeNames.GitHubPullRequest))
        {
            return FleetError.ValidationError(
                "SessionSource.SourceType",
                $"GitHub source type '{selection.Key.SourceType}' is not supported.");
        }

        GitHubSourceInput? input;
        try
        {
            input = selection.Input.Deserialize<GitHubSourceInput>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                $"Invalid GitHub session source payload: {ex.Message}");
        }

        if (input is null || string.IsNullOrWhiteSpace(input.Owner) || string.IsNullOrWhiteSpace(input.Repo) || input.Number <= 0)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                "GitHub session sources require owner, repo, and number.");
        }

        var token = await gitHubService.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return FleetError.ValidationError(
                "SessionSource.Provider",
                "GitHub must be connected before GitHub sources can be resolved.");
        }

        var basePath = string.Equals(selection.Key.SourceType, SessionSourceTypeNames.GitHubPullRequest, StringComparison.Ordinal)
            ? $"repos/{input.Owner}/{input.Repo}/pulls/{input.Number}"
            : $"repos/{input.Owner}/{input.Repo}/issues/{input.Number}";

        var itemNode = await gitHubApiProxy.FetchAsync(token, basePath, ct: cancellationToken);
        if (itemNode is not JsonObject item)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                "Unable to load the requested GitHub resource.");
        }

        var commentsNode = await gitHubApiProxy.FetchAsync(token, $"{basePath}/comments", ct: cancellationToken);
        var comments = commentsNode as JsonArray ?? [];

        var title = RedactSensitiveContent(item["title"]?.GetValue<string>() ?? $"{input.Owner}/{input.Repo}#{input.Number}");
        var body = RedactSensitiveContent(item["body"]?.GetValue<string>() ?? string.Empty);
        var htmlUrl = item["html_url"]?.GetValue<string>();

        var markdown = BuildMarkdown(
            selection.Key.SourceType,
            input.Owner,
            input.Repo,
            input.Number,
            title,
            body,
            comments);

        var truncated = markdown.Length > MaxContextCharacters;
        var preview = truncated
            ? markdown[..MaxContextCharacters]
            : markdown;

        return new ResolvedSessionSource(
            BuildDescriptor(selection.Key.SourceType),
            new ResolvedSessionInput(
                null,
                new ContextEnvelope(
                    OriginLabel: $"GitHub {GetKindLabel(selection.Key.SourceType)} #{input.Number}",
                    Content: preview,
                    IsTruncated: truncated,
                    CharacterCount: markdown.Length),
                new ProvenanceRecord(
                    ProviderId,
                    selection.Key.SourceType,
                    SessionSourceActions.AddToSession,
                    $"{input.Owner}/{input.Repo}#{input.Number}",
                    htmlUrl,
                    BuildTitle(selection.Key.SourceType, input.Owner, input.Repo, input.Number),
                    BuildSummary(input.Owner, input.Repo, input.Number),
                    DateTime.UtcNow.ToString("O"))));
    }

    private static SessionSourceDescriptor BuildDescriptor(string sourceType)
    {
        var displayName = string.Equals(sourceType, SessionSourceTypeNames.GitHubPullRequest, StringComparison.Ordinal)
            ? "GitHub pull request"
            : "GitHub issue";

        return new SessionSourceDescriptor(
            new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.GitHub,
                SourceType = sourceType,
                ActionId = SessionSourceActions.AddToSession,
                ContractVersion = 1
            },
            displayName,
            SessionSourceKinds.Context,
            [
                new SessionSourceInputField("owner", "string", true, null, "GitHub repository owner."),
                new SessionSourceInputField("repo", "string", true, null, "GitHub repository name."),
                new SessionSourceInputField("number", "number", true, null, "Issue or pull request number.")
            ],
            ProducesWorkspace: false,
            ProducesContext: true,
            RequiresConfirmation: true);
    }

    private static string BuildMarkdown(string sourceType, string owner, string repo, int number, string title, string body, JsonArray comments)
    {
        var builder = new StringBuilder();
        var resourceType = GetKindLabel(sourceType);
        builder.AppendLine(CultureInfo.InvariantCulture, $"# GitHub {resourceType}: {owner}/{repo}#{number}");
        builder.AppendLine();
        builder.AppendLine($"## Title");
        builder.AppendLine(title);
        builder.AppendLine();
        builder.AppendLine("## Body");
        builder.AppendLine(string.IsNullOrWhiteSpace(body) ? "_No description provided._" : body.Trim());

        if (comments.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Comments");

            foreach (var commentNode in comments.OfType<JsonObject>())
            {
                var author = commentNode["user"]?["login"]?.GetValue<string>() ?? "unknown";
                var commentBody = RedactSensitiveContent(commentNode["body"]?.GetValue<string>() ?? string.Empty);
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"### @{author}");
                builder.AppendLine(string.IsNullOrWhiteSpace(commentBody) ? "_No comment body provided._" : commentBody.Trim());
            }
        }

        return builder.ToString().Trim();
    }

    private static string RedactSensitiveContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var insideSecretBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (StartsSecretBlock(line))
            {
                insideSecretBlock = true;
                lines[i] = "[redacted: potential secret]";
                continue;
            }

            if (insideSecretBlock)
            {
                var isEndBoundary = IsSecretBlockBoundary(line);
                lines[i] = "[redacted: potential secret]";
                if (isEndBoundary)
                    insideSecretBlock = false;

                continue;
            }

            if (ContainsSecretLikeContent(line))
                lines[i] = "[redacted: potential secret]";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool StartsSecretBlock(string line) =>
        line.Contains("-----BEGIN ", StringComparison.OrdinalIgnoreCase)
        && line.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecretBlockBoundary(string line) =>
        line.Contains("-----END ", StringComparison.OrdinalIgnoreCase)
        && line.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSecretLikeContent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        foreach (var indicator in SecretIndicators)
        {
            var index = line.IndexOf(indicator, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && HasValueAfterIndicator(line, index + indicator.Length))
                return true;
        }

        return false;
    }

    private static bool HasValueAfterIndicator(string line, int startIndex)
    {
        for (var i = startIndex; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]) || Array.IndexOf(SecretDelimiters, line[i]) >= 0)
                continue;

            return true;
        }

        return false;
    }

    private static string BuildTitle(string sourceType, string owner, string repo, int number) =>
        $"GitHub {GetKindLabel(sourceType)} {owner}/{repo}#{number}";

    private static string BuildSummary(string owner, string repo, int number) =>
        $"GitHub {owner}/{repo}#{number}";

    private static string GetKindLabel(string sourceType) =>
        string.Equals(sourceType, SessionSourceTypeNames.GitHubPullRequest, StringComparison.Ordinal)
            ? "pull request"
            : "issue";

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record GitHubSourceInput
    {
        public string? Owner { get; init; }
        public string? Repo { get; init; }
        public int Number { get; init; }
    }
}
