using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.SessionSources;

public sealed class GitHubSessionSourceProvider(
    GitHubService gitHubService,
    GitHubApiProxy gitHubApiProxy,
    IUserContext userContext) : ISessionSourceProvider
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

    public string ProviderId => SessionSourceProviderIds.GitHub;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() =>
    [
        BuildDescriptor(SessionSourceTypeNames.GitHubIssue, SessionSourceActions.StartSession),
        BuildDescriptor(SessionSourceTypeNames.GitHubPullRequest, SessionSourceActions.StartSession),
        BuildDescriptor(SessionSourceTypeNames.GitHubIssue, SessionSourceActions.AddToSession),
        BuildDescriptor(SessionSourceTypeNames.GitHubPullRequest, SessionSourceActions.AddToSession)
    ];

    public async Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (selection.Input.ValueKind != JsonValueKind.Object)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                "Session source input must be a JSON object.");
        }

        if (!string.Equals(selection.Key.ActionId, SessionSourceActions.AddToSession, StringComparison.Ordinal)
            && !string.Equals(selection.Key.ActionId, SessionSourceActions.StartSession, StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "SessionSource.ActionId",
                "GitHub session sources currently support start-session and add-to-session.");
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
            input = selection.Input.Deserialize(InfrastructureJsonContext.Default.GitHubSourceInput);
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

        var requiresWorkspace = string.Equals(selection.Key.ActionId, SessionSourceActions.StartSession, StringComparison.Ordinal);
        if (requiresWorkspace && string.IsNullOrWhiteSpace(input.RepositoryPath))
        {
            return FleetError.ValidationError(
                "SessionSource.Input.RepositoryPath",
                "GitHub start-session sources require a repositoryPath.");
        }

        var token = await gitHubService.GetTokenAsync(
            userContext.UserId,
            cancellationToken);
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
            BuildDescriptor(selection.Key.SourceType, selection.Key.ActionId),
            new ResolvedSessionInput(
                requiresWorkspace
                    ? new WorkspaceIntent(
                        input.RepositoryPath!.Trim(),
                        NormalizeIsolationStrategy(input.IsolationStrategy),
                        NormalizeBranch(input.Branch, input.IsolationStrategy))
                    : null,
                new ContextEnvelope(
                    OriginLabel: $"GitHub {GetKindLabel(selection.Key.SourceType)} #{input.Number}",
                    Content: preview,
                    IsTruncated: truncated,
                    CharacterCount: markdown.Length),
                new ProvenanceRecord(
                    ProviderId,
                    selection.Key.SourceType,
                    selection.Key.ActionId,
                    $"{input.Owner}/{input.Repo}#{input.Number}",
                    htmlUrl,
                    BuildTitle(selection.Key.SourceType, input.Owner, input.Repo, input.Number),
                    BuildSummary(input.Owner, input.Repo, input.Number),
                    DateTime.UtcNow.ToString("O"))));
    }

    private static SessionSourceDescriptor BuildDescriptor(string sourceType, string actionId)
    {
        var displayName = string.Equals(sourceType, SessionSourceTypeNames.GitHubPullRequest, StringComparison.Ordinal)
            ? "GitHub pull request"
            : "GitHub issue";
        var isStartSession = string.Equals(actionId, SessionSourceActions.StartSession, StringComparison.Ordinal);

        return new SessionSourceDescriptor(
            new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.GitHub,
                SourceType = sourceType,
                ActionId = actionId,
                ContractVersion = 1
            },
            displayName,
            isStartSession ? SessionSourceKinds.Hybrid : SessionSourceKinds.Context,
            isStartSession
                ?
                [
                    new SessionSourceInputField("owner", "string", true, null, "GitHub repository owner."),
                    new SessionSourceInputField("repo", "string", true, null, "GitHub repository name."),
                    new SessionSourceInputField("number", "number", true, null, "Issue or pull request number."),
                    new SessionSourceInputField("repositoryPath", "string", true, null, "Canonical local repository directory path."),
                    new SessionSourceInputField("isolationStrategy", "string", false, ["existing", "worktree", "clone"], "Repository workspace isolation mode."),
                    new SessionSourceInputField("branch", "string", false, null, "Optional branch for isolated workspaces.")
                ]
                :
                [
                    new SessionSourceInputField("owner", "string", true, null, "GitHub repository owner."),
                    new SessionSourceInputField("repo", "string", true, null, "GitHub repository name."),
                    new SessionSourceInputField("number", "number", true, null, "Issue or pull request number.")
                ],
            ProducesWorkspace: isStartSession,
            ProducesContext: true,
            RequiresConfirmation: !isStartSession);
    }

    private static string NormalizeIsolationStrategy(string? isolationStrategy)
    {
        var normalized = string.IsNullOrWhiteSpace(isolationStrategy)
            ? "worktree"
            : isolationStrategy.Trim();

        return normalized is "existing" or "worktree" or "clone"
            ? normalized
            : "worktree";
    }

    private static string? NormalizeBranch(string? branch, string? isolationStrategy)
    {
        if (string.Equals(NormalizeIsolationStrategy(isolationStrategy), "existing", StringComparison.Ordinal))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(branch)
            ? null
            : branch.Trim();
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
}
