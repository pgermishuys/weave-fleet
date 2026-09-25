using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

/// <summary>
/// The one place that talks to GitHub about smart links. Each cycle it stores links the
/// <see cref="SmartLinkDetector"/> found, adds links for sessions started from GitHub, looks up the pull
/// request opened from each busy session's branch, and refreshes link details. Sessions stay running until
/// archived, so quiet ones are checked less often. Conditional requests keep unchanged responses off the
/// rate limit, and a request is sent once per cycle however many sessions share it. Changes are pushed to
/// the owner on the "sessions" topic.
/// </summary>
internal sealed partial class SmartLinkWatcherService(
    IServiceScopeFactory scopeFactory,
    GitHubApiProxy gitHubApiProxy,
    SmartLinkDetector detector,
    IEventBroadcaster broadcaster,
    RepositoryService repositoryService,
    ILogger<SmartLinkWatcherService> logger,
    TimeProvider? timeProvider = null) : BackgroundService, ISmartLinkWatcher
{
    internal const string UpdatedEventType = "smart_link.updated";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuietRefreshInterval = TimeSpan.FromMinutes(5);
    // A session counts as busy for this long after its last event, long enough to see its CI finish.
    private static readonly TimeSpan BusyWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SourceSyncInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BranchLookupInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NotFoundBackoff = TimeSpan.FromMinutes(30);
    private const int DueBatchSize = 100;
    private const int MaxLogLines = 200;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _userPausedUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Owner, string Repo)?> _remotes = new(StringComparer.Ordinal);

    private DateTimeOffset _lastSourceSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBranchLookup = DateTimeOffset.MinValue;
    private Dictionary<string, string> _branchBySession = new(StringComparer.Ordinal);

    public void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISmartLinkRepository>();
            await repository.DeleteOrphanedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCycleError(ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCycleError(ex);
            }

            try
            {
                await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogStopped();
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wake = _wake.WaitAsync(waitCts.Token);
        var detected = detector.Reader.WaitToReadAsync(waitCts.Token).AsTask();
        var delay = Task.Delay(Tick, waitCts.Token);
        await Task.WhenAny(wake, detected, delay).ConfigureAwait(false);
        await waitCts.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISmartLinkRepository>();
        var gitHubService = scope.ServiceProvider.GetRequiredService<GitHubService>();
        var cycle = new Cycle(gitHubService, gitHubApiProxy);
        var now = _time.GetUtcNow();
        var busy = detector.ActiveSince(now - BusyWindow);

        await StoreDetectedAsync(repository, ct).ConfigureAwait(false);

        if (now - _lastSourceSync >= SourceSyncInterval)
        {
            _lastSourceSync = now;
            await repository.InsertMissingSourceLinksAsync(sessionId: null, ct).ConfigureAwait(false);
        }

        if (now - _lastBranchLookup >= BranchLookupInterval)
        {
            // The first lookup after startup covers every session; after that a branch only gets a new
            // pull request when someone works in it.
            var onlySessions = _lastBranchLookup == DateTimeOffset.MinValue ? null : busy;
            _lastBranchLookup = now;
            await LookUpBranchPullRequestsAsync(repository, cycle, onlySessions, ct).ConfigureAwait(false);
        }

        await RefreshDueLinksAsync(repository, cycle, busy, ct).ConfigureAwait(false);
    }

    // ── Detection ────────────────────────────────────────────────────────────

    private async Task StoreDetectedAsync(ISmartLinkRepository repository, CancellationToken ct)
    {
        while (detector.Reader.TryRead(out var detected))
        {
            var stored = await repository.InsertDetectedAsync(
                NewLink(detected.SessionId, detected.UserId, detected.Reference, detected.Relationship),
                restoreDismissed: false,
                ct).ConfigureAwait(false);

            if (stored is not null)
                await BroadcastAsync(stored, ct).ConfigureAwait(false);
        }
    }

    private static SmartLink NewLink(string sessionId, string userId, GitHubLinkReference reference, string relationship) => new()
    {
        Id = Guid.NewGuid().ToString(),
        SessionId = sessionId,
        Url = reference.Url,
        ProviderId = "github",
        ResourceType = reference.ResourceType,
        ResourceId = reference.ResourceId,
        Title = reference.ResourceId,
        UserId = userId,
        Relationship = relationship,
    };

    // ── Branch lookup ────────────────────────────────────────────────────────

    /// <summary>
    /// Finds pull requests opened from each session's branch, for the sessions in
    /// <paramref name="onlySessions"/>, or every session when it is null.
    /// </summary>
    private async Task LookUpBranchPullRequestsAsync(
        ISmartLinkRepository repository,
        Cycle cycle,
        IReadOnlySet<string>? onlySessions,
        CancellationToken ct)
    {
        var targets = await repository.ListBranchTargetsAsync(ct).ConfigureAwait(false);
        _branchBySession = targets.ToDictionary(t => t.SessionId, t => t.Branch, StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested || IsPaused(target.UserId))
                continue;
            if (onlySessions is not null && !onlySessions.Contains(target.SessionId))
                continue;

            var token = await cycle.GetTokenAsync(target.UserId, ct).ConfigureAwait(false);
            if (token is null)
                continue;

            var remote = await ResolveGitHubRemoteAsync(target.SourceDirectory ?? target.Directory, ct).ConfigureAwait(false);
            if (remote is not { } repo)
                continue;

            // Sessions on the same branch ask the same question; the cycle sends it once.
            var head = Uri.EscapeDataString($"{repo.Owner}:{target.Branch}");
            var response = await cycle.GetAsync(
                token,
                $"repos/{repo.Owner}/{repo.Repo}/pulls?head={head}&state=all&per_page=5",
                ct).ConfigureAwait(false);

            if (HandleRateLimit(target.UserId, response) || response.Body is not JsonArray pulls)
                continue;

            foreach (var pull in pulls.OfType<JsonObject>())
            {
                var number = pull["number"]?.GetValue<int>() ?? 0;
                if (number <= 0)
                    continue;

                var reference = new GitHubLinkReference(repo.Owner, repo.Repo, number, GitHubLinkReference.PullRequest);
                var stored = await repository.InsertDetectedAsync(
                    NewLink(target.SessionId, target.UserId, reference, SmartLinkRelationships.Own),
                    restoreDismissed: false,
                    ct).ConfigureAwait(false);

                if (stored is not null)
                    await BroadcastAsync(stored, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<(string Owner, string Repo)?> ResolveGitHubRemoteAsync(string directory, CancellationToken ct)
    {
        if (_remotes.TryGetValue(directory, out var cached))
            return cached;

        var info = await repositoryService.GetRepositoryInfoAsync(directory, ct).ConfigureAwait(false);
        var remote = ParseGitHubRemote(info?.RemoteUrl);
        _remotes[directory] = remote;
        return remote;
    }

    internal static (string Owner, string Repo)? ParseGitHubRemote(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var match = GitHubRemoteRegex().Match(remoteUrl.Trim());
        return match.Success ? (match.Groups["owner"].Value, match.Groups["repo"].Value) : null;
    }

    [GeneratedRegex(@"github\.com[:/](?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRemoteRegex();

    // ── Refresh ──────────────────────────────────────────────────────────────

    private async Task RefreshDueLinksAsync(
        ISmartLinkRepository repository,
        Cycle cycle,
        IReadOnlySet<string> busySessions,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var due = await repository.ListDueForEnrichmentAsync(
            (now - RefreshInterval).UtcDateTime.ToString("O"),
            (now - QuietRefreshInterval).UtcDateTime.ToString("O"),
            busySessions,
            DueBatchSize,
            ct).ConfigureAwait(false);

        foreach (var link in due)
        {
            if (ct.IsCancellationRequested)
                break;

            // A manual refresh clears last_checked_at; that overrides backoff.
            if (link.LastCheckedAt is not null && _retryAfter.TryGetValue(link.Id, out var retryAt) && retryAt > now)
                continue;
            if (IsPaused(link.UserId))
                continue;

            try
            {
                await RefreshLinkAsync(repository, cycle, link, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogLinkError(ex, link.Id);
                _retryAfter[link.Id] = _time.GetUtcNow() + ErrorBackoff;
            }
        }
    }

    private async Task RefreshLinkAsync(ISmartLinkRepository repository, Cycle cycle, SmartLink link, CancellationToken ct)
    {
        var before = VisibleState(link);
        var token = await cycle.GetTokenAsync(link.UserId, ct).ConfigureAwait(false);

        if (token is null)
        {
            if (link.EnrichmentStatus == SmartLinkEnrichmentStatuses.NotConnected)
                return;

            link.EnrichmentStatus = SmartLinkEnrichmentStatuses.NotConnected;
        }
        else
        {
            _branchBySession.TryGetValue(link.SessionId, out var sessionBranch);
            var outcome = await EnrichAsync(link, cycle, token, sessionBranch, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case EnrichOutcome.RateLimited:
                    return;
                case EnrichOutcome.NotFound:
                    _retryAfter[link.Id] = _time.GetUtcNow() + NotFoundBackoff;
                    break;
                // A not-connected link is due every cycle, so a token GitHub refuses needs a backoff too.
                case EnrichOutcome.NotConnected:
                case EnrichOutcome.Error:
                    _retryAfter[link.Id] = _time.GetUtcNow() + ErrorBackoff;
                    break;
                default:
                    _retryAfter.TryRemove(link.Id, out _);
                    break;
            }
        }

        var checkedAt = _time.GetUtcNow().UtcDateTime.ToString("O");
        link.LastCheckedAt = checkedAt;
        var changed = VisibleState(link) != before;
        if (changed)
            link.UpdatedAt = checkedAt;

        await repository.UpdateEnrichmentAsync(link, ct).ConfigureAwait(false);
        if (changed)
            await BroadcastAsync(link, ct).ConfigureAwait(false);
    }

    private enum EnrichOutcome
    {
        Resolved,
        NotFound,
        NotConnected,
        RateLimited,
        Error,
    }

    private async Task<EnrichOutcome> EnrichAsync(SmartLink link, Cycle cycle, string token, string? sessionBranch, CancellationToken ct)
    {
        if (!GitHubLinkParser.TryParseUrl(link.Url, out var reference))
        {
            link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Error;
            return EnrichOutcome.Error;
        }

        var metadata = ParseMetadata(link.MetadataJson);

        if (link.ResourceType != GitHubLinkReference.PullRequest)
        {
            var issueResponse = await cycle.GetAsync(
                token, $"repos/{reference.Owner}/{reference.Repo}/issues/{reference.Number}", ct).ConfigureAwait(false);

            var failure = ClassifyFailure(link, issueResponse);
            if (failure is not null)
                return failure.Value;

            var issue = (JsonObject)issueResponse.Body!;
            if (issue["pull_request"] is null)
            {
                ApplyIssue(link, reference, issue, metadata);
                link.MetadataJson = metadata.ToJsonString();
                link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Resolved;
                return EnrichOutcome.Resolved;
            }

            // owner/repo#N shorthand that turned out to be a pull request.
            link.ResourceType = GitHubLinkReference.PullRequest;
        }

        var prResponse = await cycle.GetAsync(
            token, $"repos/{reference.Owner}/{reference.Repo}/pulls/{reference.Number}", ct).ConfigureAwait(false);

        var prFailure = ClassifyFailure(link, prResponse);
        if (prFailure is not null)
            return prFailure.Value;

        var pr = (JsonObject)prResponse.Body!;
        ApplyPullRequest(link, reference, pr, metadata, sessionBranch);

        if (!link.IsTerminal)
        {
            var sha = pr["head"]?["sha"]?.GetValue<string>();
            if (sha is not null)
                await RefreshChecksAsync(link, reference, cycle, token, sha, metadata, ct).ConfigureAwait(false);

            // Review activity bumps the pull request's updated_at, so a 304 means threads are unchanged.
            if (!prResponse.NotModified || metadata["reviewThreads"] is null)
                await RefreshReviewThreadsAsync(reference, cycle, token, metadata, ct).ConfigureAwait(false);
        }

        link.MetadataJson = metadata.ToJsonString();
        link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Resolved;
        return EnrichOutcome.Resolved;
    }

    private EnrichOutcome? ClassifyFailure(SmartLink link, GitHubApiResponse response)
    {
        if (response.IsSuccess && response.Body is JsonObject)
            return null;

        if (HandleRateLimit(link.UserId, response))
            return EnrichOutcome.RateLimited;

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                link.EnrichmentStatus = SmartLinkEnrichmentStatuses.NotFound;
                return EnrichOutcome.NotFound;
            case HttpStatusCode.Unauthorized:
                link.EnrichmentStatus = SmartLinkEnrichmentStatuses.NotConnected;
                return EnrichOutcome.NotConnected;
            default:
                link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Error;
                return EnrichOutcome.Error;
        }
    }

    private bool HandleRateLimit(string userId, GitHubApiResponse response)
    {
        if (response.RateLimitResetAt is not { } resetAt)
            return false;

        _userPausedUntil[userId] = resetAt;
        LogRateLimited(resetAt);
        return true;
    }

    private bool IsPaused(string userId)
        => _userPausedUntil.TryGetValue(userId, out var until) && until > _time.GetUtcNow();

    internal static void ApplyIssue(SmartLink link, GitHubLinkReference reference, JsonObject issue, JsonObject metadata)
    {
        var closed = string.Equals(issue["state"]?.GetValue<string>(), "closed", StringComparison.Ordinal);
        link.ResourceType = GitHubLinkReference.Issue;
        link.ResourceId = reference.ResourceId;
        link.Title = FormatTitle(reference, issue);
        link.Status = closed ? "closed" : "open";
        link.StatusLabel = closed ? "Closed" : "Open";
        link.IsTerminal = closed;

        SetReferenceMetadata(metadata, reference, issue);
    }

    internal static void ApplyPullRequest(
        SmartLink link,
        GitHubLinkReference reference,
        JsonObject pr,
        JsonObject metadata,
        string? sessionBranch)
    {
        var merged = pr["merged"]?.GetValue<bool>() == true;
        var closed = string.Equals(pr["state"]?.GetValue<string>(), "closed", StringComparison.Ordinal);
        var draft = pr["draft"]?.GetValue<bool>() == true;
        var (status, label) = merged ? ("merged", "Merged")
            : closed ? ("closed", "Closed")
            : draft ? ("draft", "Draft")
            : ("open", "Open");

        link.ResourceType = GitHubLinkReference.PullRequest;
        link.ResourceId = reference.ResourceId;
        link.Title = FormatTitle(reference, pr);
        link.Status = status;
        link.StatusLabel = label;
        link.IsTerminal = merged || closed;

        SetReferenceMetadata(metadata, reference, pr);
        metadata["draft"] = draft;
        metadata["mergeable"] = pr["mergeable"] is JsonValue mergeable && mergeable.TryGetValue<bool>(out var value)
            ? JsonValue.Create(value)
            : null;

        var headRef = pr["head"]?["ref"]?.GetValue<string>();
        metadata["headRef"] = headRef;
        metadata["baseRef"] = pr["base"]?["ref"]?.GetValue<string>();
        metadata["mergedAt"] = pr["merged_at"]?.GetValue<string>();
        metadata["additions"] = pr["additions"]?.GetValue<int>();
        metadata["deletions"] = pr["deletions"]?.GetValue<int>();
        metadata["changedFiles"] = pr["changed_files"]?.GetValue<int>();
        metadata["author"] = pr["user"]?["login"]?.GetValue<string>();
        metadata["authorAvatarUrl"] = pr["user"]?["avatar_url"]?.GetValue<string>();

        // A pull request from the session's own branch belongs to the session.
        if (headRef is not null
            && sessionBranch is not null
            && string.Equals(headRef, sessionBranch, StringComparison.Ordinal)
            && SmartLinkRelationships.Rank(link.Relationship) < SmartLinkRelationships.Rank(SmartLinkRelationships.Own))
        {
            link.Relationship = SmartLinkRelationships.Own;
        }
    }

    private static string FormatTitle(GitHubLinkReference reference, JsonObject resource)
        => $"{reference.Owner}/{reference.Repo} #{reference.Number.ToString(CultureInfo.InvariantCulture)}: {resource["title"]?.GetValue<string>() ?? string.Empty}";

    private static void SetReferenceMetadata(JsonObject metadata, GitHubLinkReference reference, JsonObject resource)
    {
        metadata["owner"] = reference.Owner;
        metadata["repo"] = reference.Repo;
        metadata["number"] = reference.Number;
        metadata["htmlUrl"] = resource["html_url"]?.GetValue<string>();
        metadata["updatedAt"] = resource["updated_at"]?.GetValue<string>();

        var labels = new JsonArray();
        foreach (var label in (resource["labels"] as JsonArray ?? []).OfType<JsonObject>())
        {
            labels.Add((JsonNode)new JsonObject
            {
                ["name"] = label["name"]?.GetValue<string>() ?? string.Empty,
                ["color"] = label["color"]?.GetValue<string>() ?? string.Empty,
            });
        }

        metadata["labels"] = labels;
    }

    private async Task RefreshChecksAsync(
        SmartLink link,
        GitHubLinkReference reference,
        Cycle cycle,
        string token,
        string sha,
        JsonObject metadata,
        CancellationToken ct)
    {
        var response = await cycle.GetAsync(
            token, $"repos/{reference.Owner}/{reference.Repo}/commits/{sha}/check-runs", ct).ConfigureAwait(false);
        if (HandleRateLimit(link.UserId, response) || response.Body is null)
            return;

        var ci = GitHubEndpointMappings.BuildCiStatusResponse(sha, response.Body);
        metadata["ci"] = CiToJson(ci);

        if (string.Equals(ci.CiStatus, "failure", StringComparison.OrdinalIgnoreCase))
            await CaptureCiFailuresAsync(reference, cycle, token, sha, ci, metadata, ct).ConfigureAwait(false);
    }

    private async Task CaptureCiFailuresAsync(
        GitHubLinkReference reference,
        Cycle cycle,
        string token,
        string sha,
        GitHubCiStatusResponse ci,
        JsonObject metadata,
        CancellationToken ct)
    {
        var ciFailures = GetCiFailures(metadata);
        var newFailures = ci.CheckRuns
            .Where(IsFailedRun)
            .Where(cr => !ciFailures.Any(f =>
                string.Equals(f.Sha, sha, StringComparison.Ordinal) &&
                string.Equals(f.CheckRunName, cr.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (newFailures.Count == 0)
            return;

        foreach (var failedRun in newFailures)
        {
            if (ct.IsCancellationRequested)
                break;

            string? logContent = null;
            if (failedRun.Id > 0)
            {
                var rawLog = await cycle.FetchTextAsync(
                    token,
                    $"repos/{reference.Owner}/{reference.Repo}/actions/jobs/{failedRun.Id}/logs",
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(rawLog))
                    logContent = CiLogParser.ExtractRelevantLogLines(rawLog, MaxLogLines);
            }

            ciFailures.Add(new CiFailure(
                sha,
                failedRun.Name,
                failedRun.Id,
                failedRun.Conclusion ?? string.Empty,
                failedRun.HtmlUrl ?? string.Empty,
                logContent,
                _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        }

        SetCiFailures(metadata, ciFailures);
    }

    private static async Task RefreshReviewThreadsAsync(
        GitHubLinkReference reference,
        Cycle cycle,
        string token,
        JsonObject metadata,
        CancellationToken ct)
    {
        var variables = new JsonObject
        {
            ["owner"] = JsonValue.Create(reference.Owner),
            ["repo"] = JsonValue.Create(reference.Repo),
            ["number"] = JsonValue.Create(reference.Number),
        };

        var response = await cycle.PostGraphQLAsync(token, GitHubEndpointMappings.ReviewThreadsQuery, variables, ct).ConfigureAwait(false);
        if (response?["data"]?["repository"]?["pullRequest"] is not JsonObject pullRequest)
            return;

        metadata["reviewThreads"] = ReviewThreadsToJson(GitHubEndpointMappings.BuildReviewThreadsResponse(response));
        ApplyReviews(pullRequest, metadata);
    }

    /// <summary>The review decision and each reviewer's verdict, for the session's pull request badge and pill.</summary>
    internal static void ApplyReviews(JsonObject pullRequest, JsonObject metadata)
    {
        metadata["reviewDecision"] = pullRequest["reviewDecision"]?.GetValue<string>();

        var reviewers = new JsonArray();
        foreach (var reviewer in GitHubItems.ParseReviewers(pullRequest))
        {
            reviewers.Add((JsonNode)new JsonObject
            {
                ["login"] = reviewer.Login,
                ["avatarUrl"] = reviewer.AvatarUrl,
                ["state"] = reviewer.State,
            });
        }

        metadata["reviewers"] = reviewers;
    }

    private static bool IsFailedRun(GitHubCheckRunDto cr)
        => string.Equals(cr.Conclusion, "failure", StringComparison.OrdinalIgnoreCase)
           || string.Equals(cr.Conclusion, "timed_out", StringComparison.OrdinalIgnoreCase)
           || string.Equals(cr.Conclusion, "startup_failure", StringComparison.OrdinalIgnoreCase);

    internal static JsonObject CiToJson(GitHubCiStatusResponse ci)
    {
        var runs = new JsonArray();
        foreach (var cr in ci.CheckRuns)
        {
            runs.Add((JsonNode)new JsonObject
            {
                ["id"] = cr.Id,
                ["name"] = cr.Name,
                ["status"] = cr.Status,
                ["conclusion"] = cr.Conclusion,
                ["htmlUrl"] = cr.HtmlUrl,
                ["workflowName"] = cr.WorkflowName,
                ["startedAt"] = cr.StartedAt,
                ["completedAt"] = cr.CompletedAt,
            });
        }

        return new JsonObject
        {
            ["headSha"] = ci.HeadSha,
            ["ciStatus"] = ci.CiStatus,
            ["checkRuns"] = runs,
        };
    }

    internal static JsonObject ReviewThreadsToJson(GitHubReviewThreadsResponse summary)
    {
        var threads = new JsonArray();
        foreach (var thread in summary.Threads)
        {
            var comments = new JsonArray();
            foreach (var comment in thread.Comments)
            {
                comments.Add((JsonNode)new JsonObject
                {
                    ["id"] = comment.Id,
                    ["databaseId"] = comment.DatabaseId,
                    ["body"] = comment.Body,
                    ["authorLogin"] = comment.AuthorLogin,
                    ["createdAt"] = comment.CreatedAt,
                    ["url"] = comment.Url,
                });
            }

            threads.Add((JsonNode)new JsonObject
            {
                ["threadNodeId"] = thread.ThreadNodeId,
                ["isResolved"] = thread.IsResolved,
                ["isOutdated"] = thread.IsOutdated,
                ["path"] = thread.Path,
                ["line"] = thread.Line,
                ["comments"] = comments,
            });
        }

        return new JsonObject
        {
            ["unresolvedCount"] = summary.UnresolvedCount,
            ["threads"] = threads,
        };
    }

    private static JsonObject ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(metadataJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static (string, string, string, string, string?, bool, string, string) VisibleState(SmartLink link)
        => (link.ResourceType, link.Title, link.Status, link.StatusLabel, link.MetadataJson, link.IsTerminal, link.Relationship, link.EnrichmentStatus);

    private async Task BroadcastAsync(SmartLink link, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(SmartLinkService.ToDto(link), InfrastructureJsonContext.Default.SmartLinkDto);
        await broadcaster.BroadcastAsync("sessions", UpdatedEventType, payload, link.UserId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One cycle's lookups: each user's GitHub token once, and each GitHub request once, so sessions on the
    /// same branch and links to the same pull request share a call.
    /// </summary>
    private sealed class Cycle(GitHubService gitHubService, GitHubApiProxy proxy)
    {
        private readonly Dictionary<string, string?> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GitHubApiResponse> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonNode?> _graphQL = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _texts = new(StringComparer.Ordinal);

        public async Task<string?> GetTokenAsync(string userId, CancellationToken ct)
        {
            if (!_tokens.TryGetValue(userId, out var token))
            {
                token = await gitHubService.GetTokenAsync(userId, ct).ConfigureAwait(false);
                _tokens[userId] = token;
            }

            return token;
        }

        public async Task<GitHubApiResponse> GetAsync(string token, string path, CancellationToken ct)
        {
            var key = Key(token, path);
            if (!_responses.TryGetValue(key, out var response))
            {
                response = await proxy.GetConditionalAsync(token, path, ct).ConfigureAwait(false);
                _responses[key] = response;
            }

            // A JSON node belongs to one parent, so every caller gets its own copy.
            return response with { Body = response.Body?.DeepClone() };
        }

        public async Task<JsonNode?> PostGraphQLAsync(string token, string query, JsonObject variables, CancellationToken ct)
        {
            var key = Key(token, query + variables.ToJsonString());
            if (!_graphQL.TryGetValue(key, out var response))
            {
                response = await proxy.PostGraphQLAsync(token, query, variables, ct).ConfigureAwait(false);
                _graphQL[key] = response;
            }

            return response?.DeepClone();
        }

        public async Task<string?> FetchTextAsync(string token, string path, CancellationToken ct)
        {
            var key = Key(token, path);
            if (!_texts.TryGetValue(key, out var text))
            {
                text = await proxy.FetchTextAsync(token, path, ct).ConfigureAwait(false);
                _texts[key] = text;
            }

            return text;
        }

        private static string Key(string token, string request) => $"{token}\n{request}";
    }

    // ── CI failure metadata ──────────────────────────────────────────────────

    internal static List<CiFailure> GetCiFailures(JsonObject metadata)
    {
        var result = new List<CiFailure>();
        if (metadata["ciFailures"] is not JsonArray arr)
            return result;

        foreach (var item in arr.OfType<JsonObject>())
        {
            var sha = item["sha"]?.GetValue<string>();
            var checkRunName = item["checkRunName"]?.GetValue<string>();
            if (sha is null || checkRunName is null)
                continue;

            var checkRunId = item["checkRunId"]?.GetValue<long>() ?? 0;
            var conclusion = item["conclusion"]?.GetValue<string>() ?? string.Empty;
            var htmlUrl = item["htmlUrl"]?.GetValue<string>() ?? string.Empty;
            var logContent = item["logContent"]?.GetValue<string>();
            var detectedAt = item["detectedAt"]?.GetValue<string>() ?? string.Empty;
            result.Add(new CiFailure(sha, checkRunName, checkRunId, conclusion, htmlUrl, logContent, detectedAt));
        }

        return result;
    }

    internal static void SetCiFailures(JsonObject metadata, List<CiFailure> failures)
    {
        var arr = new JsonArray();
        foreach (var f in failures)
        {
            var entry = new JsonObject
            {
                ["sha"] = JsonValue.Create(f.Sha),
                ["checkRunName"] = JsonValue.Create(f.CheckRunName),
                ["checkRunId"] = JsonValue.Create(f.CheckRunId),
                ["conclusion"] = JsonValue.Create(f.Conclusion),
                ["htmlUrl"] = JsonValue.Create(f.HtmlUrl),
                ["logContent"] = f.LogContent is not null ? JsonValue.Create(f.LogContent) : null,
                ["detectedAt"] = JsonValue.Create(f.DetectedAt),
            };
            arr.Add((JsonNode)entry);
        }
        metadata["ciFailures"] = arr;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartLinkWatcherService started.")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartLinkWatcherService stopped.")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error in SmartLinkWatcherService cycle.")]
    private partial void LogCycleError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error refreshing smart link {LinkId}.")]
    private partial void LogLinkError(Exception ex, string linkId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub rate limit reached; smart links pause until {ResetAt}.")]
    private partial void LogRateLimited(DateTimeOffset resetAt);

    internal sealed record CiFailure(
        string Sha,
        string CheckRunName,
        long CheckRunId,
        string Conclusion,
        string HtmlUrl,
        string? LogContent,
        string DetectedAt);
}
