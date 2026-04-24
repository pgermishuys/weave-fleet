using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

public sealed class BoardSyncService(
    IBoardRepository boardRepository,
    GitHubService gitHubService,
    GitHubApiProxy gitHubApiProxy,
    IUserContext userContext) : IBoardSyncService
{
    private const int GitHubPageSize = 100;
    private const string GitHubProviderType = "github";
    private const string GitHubIssueKeyPrefix = "github:";

    public Task<Result<BoardSyncResult>> SyncAsync(string boardId)
        => SyncAsync(boardId, CancellationToken.None);

    public async Task<Result<BoardSyncResult>> SyncAsync(string boardId, CancellationToken cancellationToken)
    {
        var board = await boardRepository.GetByIdAsync(boardId, userContext.UserId).ConfigureAwait(false);
        if (board is null)
            return FleetError.NotFoundFor(nameof(Board), boardId);

        var sources = await boardRepository.GetSourcesByBoardIdAsync(boardId, userContext.UserId).ConfigureAwait(false);
        if (sources.Count == 0)
            return Result.Success(CreateResult(0, 0, 0, 0, 0, DateTimeOffset.UtcNow.ToString("O")));

        var lanes = await boardRepository.ListLanesAsync(boardId, userContext.UserId).ConfigureAwait(false);
        var inboxLane = lanes.FirstOrDefault(lane => lane.IsInbox);
        if (inboxLane is null)
            return FleetError.ValidationError(nameof(BoardLane), "Board must have an inbox lane before syncing.");

        var cards = await boardRepository.ListCardsAsync(boardId, userContext.UserId).ConfigureAwait(false);
        var cardsByKey = cards
            .Where(card => !string.IsNullOrWhiteSpace(card.SourceType) && !string.IsNullOrWhiteSpace(card.SourceKey))
            .ToDictionary(card => BuildCardLookupKey(card.SourceType!, card.SourceKey!), StringComparer.Ordinal);

        var sourcesProcessed = 0;
        var issuesFetched = 0;
        var cardsCreated = 0;
        var cardsUpdated = 0;
        var cardsMarkedStale = 0;
        var syncedAt = DateTimeOffset.UtcNow.ToString("O");
        var fetchedSourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var trackedSourcePrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(source.ProviderType, GitHubProviderType, StringComparison.OrdinalIgnoreCase))
            {
                return FleetError.ValidationError(
                    nameof(BoardSource.ProviderType),
                    $"Board source provider '{source.ProviderType}' is not supported.");
            }

            var configResult = ParseGitHubSourceConfig(source.Config);
            if (configResult.IsFailure)
                return configResult.Error;

            trackedSourcePrefixes.Add(BuildGitHubIssueSourceKeyPrefix(configResult.Value.Repository));

            var issuesResult = await FetchIssuesAsync(configResult.Value, cancellationToken).ConfigureAwait(false);
            if (issuesResult.IsFailure)
                return issuesResult.Error;

            foreach (var issue in issuesResult.Value)
            {
                fetchedSourceKeys.Add(issue.SourceKey);

                var cardLookupKey = BuildCardLookupKey(SessionSourceTypeNames.GitHubIssue, issue.SourceKey);
                if (cardsByKey.TryGetValue(cardLookupKey, out var existingCard))
                {
                    var updatedCard = new BoardCard
                    {
                        Id = existingCard.Id,
                        BoardId = existingCard.BoardId,
                        LaneId = existingCard.LaneId,
                        Title = issue.Title,
                        SourceType = SessionSourceTypeNames.GitHubIssue,
                        SourceKey = issue.SourceKey,
                        Metadata = issue.Metadata,
                        Position = existingCard.Position,
                        ArchivedAt = existingCard.ArchivedAt,
                        CreatedAt = existingCard.CreatedAt,
                        UpdatedAt = syncedAt
                    };

                    await boardRepository.UpdateCardAsync(updatedCard).ConfigureAwait(false);
                    cardsByKey[cardLookupKey] = updatedCard;
                    cardsUpdated++;
                    continue;
                }

                var createdCard = new BoardCard
                {
                    Id = Guid.NewGuid().ToString(),
                    BoardId = boardId,
                    LaneId = inboxLane.Id,
                    Title = issue.Title,
                    SourceType = SessionSourceTypeNames.GitHubIssue,
                    SourceKey = issue.SourceKey,
                    Metadata = issue.Metadata,
                    Position = 0,
                    ArchivedAt = null,
                    CreatedAt = syncedAt,
                    UpdatedAt = syncedAt
                };

                await boardRepository.InsertCardAsync(createdCard).ConfigureAwait(false);
                var persistedCard = await boardRepository.GetCardByIdAsync(boardId, createdCard.Id, userContext.UserId).ConfigureAwait(false)
                    ?? createdCard;

                cardsByKey[cardLookupKey] = persistedCard;
                cardsCreated++;
            }

            source.LastSyncAt = syncedAt;
            source.UpdatedAt = syncedAt;
            await boardRepository.UpdateSourceAsync(source).ConfigureAwait(false);

            sourcesProcessed++;
            issuesFetched += issuesResult.Value.Count;
        }

        var staleCards = cardsByKey.Values
            .Where(card => string.Equals(card.SourceType, SessionSourceTypeNames.GitHubIssue, StringComparison.Ordinal))
            .Where(card => card.SourceKey is not null)
            .Where(card => trackedSourcePrefixes.Any(prefix => card.SourceKey!.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(card => !fetchedSourceKeys.Contains(card.SourceKey!))
            .ToList();

        foreach (var staleCard in staleCards)
        {
            var staleMetadataResult = MarkMetadataAsStale(staleCard.Metadata);
            if (!staleMetadataResult.WasMarked)
                continue;

            var updatedStaleCard = new BoardCard
            {
                Id = staleCard.Id,
                BoardId = staleCard.BoardId,
                LaneId = staleCard.LaneId,
                Title = staleCard.Title,
                SourceType = staleCard.SourceType,
                SourceKey = staleCard.SourceKey,
                Metadata = staleMetadataResult.Metadata,
                Position = staleCard.Position,
                ArchivedAt = staleCard.ArchivedAt,
                CreatedAt = staleCard.CreatedAt,
                UpdatedAt = syncedAt
            };

            await boardRepository.UpdateCardAsync(updatedStaleCard).ConfigureAwait(false);
            cardsByKey[BuildCardLookupKey(updatedStaleCard.SourceType!, updatedStaleCard.SourceKey!)] = updatedStaleCard;
            cardsMarkedStale++;
        }

        return Result.Success(CreateResult(sourcesProcessed, issuesFetched, cardsCreated, cardsUpdated, cardsMarkedStale, syncedAt));
    }

    private async Task<Result<IReadOnlyList<GitHubIssueCard>>> FetchIssuesAsync(
        GitHubBoardSourceConfig config,
        CancellationToken cancellationToken)
    {
        var token = await gitHubService.GetTokenAsync(userContext.UserId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return FleetError.Unauthorized;

        var assigneeResult = await ResolveAssigneeAsync(config.Assignee, cancellationToken).ConfigureAwait(false);
        if (assigneeResult.IsFailure)
            return assigneeResult.Error;

        var issues = new List<GitHubIssueCard>();

        for (var page = 1; ; page++)
        {
            var path = BuildGitHubIssuesPath(config, assigneeResult.Value, page);
            var response = await gitHubApiProxy.FetchAsync(token, path, "GET", null, cancellationToken).ConfigureAwait(false);
            if (response is not JsonArray issueArray)
            {
                return FleetError.ValidationError(
                    nameof(BoardSource.Config),
                    $"GitHub source '{config.Repository}' returned an unexpected issues payload.");
            }

            foreach (var node in issueArray)
            {
                var issue = ParseIssueCard(node, config);
                if (issue is not null)
                    issues.Add(issue);
            }

            if (issueArray.Count < GitHubPageSize)
                break;
        }

        return Result.Success<IReadOnlyList<GitHubIssueCard>>(issues);
    }

    private async Task<Result<string?>> ResolveAssigneeAsync(string? assignee, CancellationToken cancellationToken)
    {
        if (!string.Equals(assignee, "@me", StringComparison.Ordinal))
            return Result.Success(assignee);

        var gitHubLogin = await gitHubService.GetGitHubLoginAsync(userContext.UserId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(gitHubLogin))
            return Result.Success<string?>(gitHubLogin);

        return FleetError.ValidationError(
            nameof(BoardSource.Config),
            "GitHub board source 'assignee' value '@me' requires a stored GitHub login.");
    }

    private static Result<GitHubBoardSourceConfig> ParseGitHubSourceConfig(string config)
    {
        JsonObject? json;

        try
        {
            json = JsonNode.Parse(config) as JsonObject;
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError(nameof(BoardSource.Config), $"Board source config is invalid JSON: {ex.Message}");
        }

        if (json is null)
            return FleetError.ValidationError(nameof(BoardSource.Config), "Board source config must be a JSON object.");

        var repository = json["repository"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(repository))
        {
            var owner = json["owner"]?.GetValue<string>()?.Trim();
            var repo = json["repo"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
                repository = $"{owner}/{repo}";
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            return FleetError.ValidationError(
                nameof(BoardSource.Config),
                "GitHub board sources require a 'repository' value in the form 'owner/repo'.");
        }

        var repositoryParts = repository.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repositoryParts.Length != 2)
        {
            return FleetError.ValidationError(
                nameof(BoardSource.Config),
                $"GitHub repository '{repository}' must be in the form 'owner/repo'.");
        }

        var state = json["state"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(state))
            state = "open";

        var labels = ParseLabels(json["labels"]);
        var assigneeResult = ParseAssignee(json["assignee"]);
        if (assigneeResult.IsFailure)
            return assigneeResult.Error;

        return Result.Success(new GitHubBoardSourceConfig(repositoryParts[0], repositoryParts[1], repository, state, labels, assigneeResult.Value));
    }

    private static string? ParseLabels(JsonNode? labelsNode)
    {
        if (labelsNode is null)
            return null;

        if (labelsNode is JsonValue)
        {
            var labelsValue = labelsNode.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(labelsValue) ? null : labelsValue;
        }

        if (labelsNode is not JsonArray labelsArray)
            return null;

        var labels = labelsArray
            .Select(labelNode => labelNode?.GetValue<string>()?.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Cast<string>()
            .ToList();

        return labels.Count == 0 ? null : string.Join(',', labels);
    }

    private static Result<string?> ParseAssignee(JsonNode? assigneeNode)
    {
        if (assigneeNode is null)
            return Result.Success<string?>(null);

        if (assigneeNode is not JsonValue)
        {
            return FleetError.ValidationError(
                nameof(BoardSource.Config),
                "GitHub board source 'assignee' must be a string when provided.");
        }

        var assignee = assigneeNode.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(assignee))
            return Result.Success<string?>(null);

        if (string.Equals(assignee, "@me", StringComparison.Ordinal))
            return Result.Success<string?>(assignee);

        if (IsValidGitHubLogin(assignee))
            return Result.Success<string?>(assignee);

        return FleetError.ValidationError(
            nameof(BoardSource.Config),
            $"GitHub board source 'assignee' must be '@me' or a valid GitHub login. Received '{assignee}'.");
    }

    private static bool IsValidGitHubLogin(string assignee)
    {
        if (assignee.Length is 0 or > 39)
            return false;

        if (assignee[0] == '-' || assignee[^1] == '-')
            return false;

        for (var index = 0; index < assignee.Length; index++)
        {
            var character = assignee[index];
            var isLetterOrDigit = char.IsLetterOrDigit(character);
            if (!isLetterOrDigit && character != '-')
                return false;

            if (character == '-' && index > 0 && assignee[index - 1] == '-')
                return false;
        }

        return true;
    }

    private static GitHubIssueCard? ParseIssueCard(JsonNode? node, GitHubBoardSourceConfig config)
    {
        if (node is not JsonObject issue)
            return null;

        if (issue["pull_request"] is not null)
            return null;

        var issueNumber = issue["number"]?.GetValue<int?>();
        var title = issue["title"]?.GetValue<string>()?.Trim();
        if (issueNumber is null || string.IsNullOrWhiteSpace(title))
            return null;

        var labels = new JsonArray();
        if (issue["labels"] is JsonArray labelArray)
        {
            foreach (var label in labelArray)
            {
                var labelName = label?["name"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(labelName))
                    labels.Add(labelName);
            }
        }

        var metadata = new JsonObject
        {
            ["number"] = issueNumber.Value,
            ["state"] = issue["state"]?.GetValue<string>(),
            ["labels"] = labels,
            ["assignee"] = issue["assignee"]?["login"]?.GetValue<string>(),
            ["html_url"] = issue["html_url"]?.GetValue<string>(),
            ["updated_at"] = issue["updated_at"]?.GetValue<string>()
        }.ToJsonString();

        return new GitHubIssueCard(title, BuildGitHubIssueSourceKey(config.Repository, issueNumber.Value), metadata);
    }

    private static StaleMetadataResult MarkMetadataAsStale(string? metadata)
    {
        JsonObject json;

        try
        {
            json = JsonNode.Parse(metadata ?? "{}") as JsonObject ?? [];
        }
        catch (JsonException)
        {
            json = [];
        }

        var isAlreadyStale = json["stale"]?.GetValue<bool>() == true;
        if (isAlreadyStale)
            return new StaleMetadataResult(json.ToJsonString(), false);

        json["stale"] = true;
        return new StaleMetadataResult(json.ToJsonString(), true);
    }

    private static string BuildGitHubIssuesPath(GitHubBoardSourceConfig config, string? assignee, int page)
    {
        var query = BuildQuery(
            ("assignee", assignee),
            ("state", config.State),
            ("labels", config.Labels),
            ("page", page.ToString(CultureInfo.InvariantCulture)),
            ("per_page", GitHubPageSize.ToString(CultureInfo.InvariantCulture)));

        return $"repos/{config.Owner}/{config.Repo}/issues{query}";
    }

    private static string BuildQuery(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");

        var queryString = string.Join("&", parts);
        return queryString.Length == 0 ? string.Empty : $"?{queryString}";
    }

    private static string BuildGitHubIssueSourceKey(string repository, int issueNumber)
        => $"{BuildGitHubIssueSourceKeyPrefix(repository)}{issueNumber}";

    private static string BuildGitHubIssueSourceKeyPrefix(string repository)
        => $"{GitHubIssueKeyPrefix}{repository}#";

    private static string BuildCardLookupKey(string sourceType, string sourceKey)
        => $"{sourceType}:{sourceKey}";

    private static BoardSyncResult CreateResult(
        int sourcesProcessed,
        int issuesFetched,
        int cardsCreated,
        int cardsUpdated,
        int cardsMarkedStale,
        string syncedAt)
        => new(
            sourcesProcessed,
            issuesFetched,
            cardsCreated,
            cardsUpdated,
            cardsMarkedStale,
            syncedAt);

    private sealed record GitHubBoardSourceConfig(
        string Owner,
        string Repo,
        string Repository,
        string State,
        string? Labels,
        string? Assignee);

    private sealed record GitHubIssueCard(string Title, string SourceKey, string Metadata);

    private sealed record StaleMetadataResult(string Metadata, bool WasMarked);
}
