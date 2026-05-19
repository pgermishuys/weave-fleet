using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

/// <summary>
/// Background service that watches non-terminal PR smart links for CI failures and
/// stores failure details (including logs) in smart link metadata for user-initiated diagnosis.
/// </summary>
internal sealed partial class CiWatcherService(
    IServiceScopeFactory scopeFactory,
    GitHubApiProxy gitHubApiProxy,
    ILogger<CiWatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int MaxLogLines = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        // Reconcile: remove smart links whose sessions no longer exist
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISmartLinkRepository>();
            await repository.DeleteOrphanedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllPrLinksAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPollError(ex);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }

        LogStopped();
    }

    private async Task PollAllPrLinksAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISmartLinkRepository>();
        var gitHubService = scope.ServiceProvider.GetRequiredService<GitHubService>();

        var prLinks = await repository.ListNonTerminalPrLinksAsync(ct).ConfigureAwait(false);

        foreach (var link in prLinks)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessPrLinkAsync(link, repository, gitHubService, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogLinkError(ex, link.Id);
            }
        }
    }

    private async Task ProcessPrLinkAsync(
        Domain.Entities.SmartLink link,
        ISmartLinkRepository repository,
        GitHubService gitHubService,
        CancellationToken ct)
    {
        // Parse existing metadata
        JsonObject metadata;
        try
        {
            metadata = string.IsNullOrWhiteSpace(link.MetadataJson)
                ? new JsonObject()
                : (JsonNode.Parse(link.MetadataJson) as JsonObject ?? new JsonObject());
        }
        catch (JsonException)
        {
            metadata = new JsonObject();
        }

        var owner = metadata["owner"]?.GetValue<string>();
        var repo = metadata["repo"]?.GetValue<string>();
        var number = metadata["number"]?.GetValue<int>();

        if (owner is null || repo is null || number is null)
            return;

        var token = await gitHubService.GetTokenAsync(link.UserId, ct).ConfigureAwait(false);
        if (token is null)
            return;

        // Fetch PR to get head SHA
        var pr = await gitHubApiProxy.FetchAsync(token, $"repos/{owner}/{repo}/pulls/{number}", ct: ct).ConfigureAwait(false);
        if (pr is null) return;

        var sha = pr["head"]?["sha"]?.GetValue<string>();
        if (sha is null) return;

        // Fetch check runs
        var checkRunsNode = await gitHubApiProxy.FetchAsync(token, $"repos/{owner}/{repo}/commits/{sha}/check-runs", ct: ct).ConfigureAwait(false);
        var ciStatusResponse = GitHubEndpointMappings.BuildCiStatusResponse(sha, checkRunsNode);

        // Only act on failures
        if (!string.Equals(ciStatusResponse.CiStatus, "failure", StringComparison.OrdinalIgnoreCase))
            return;

        // Load existing CI failures to dedup
        var ciFailures = GetCiFailures(metadata);

        // Find failed check runs not yet stored
        var failedRuns = ciStatusResponse.CheckRuns
            .Where(cr => string.Equals(cr.Conclusion, "failure", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(cr.Conclusion, "timed_out", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(cr.Conclusion, "startup_failure", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var newFailures = failedRuns
            .Where(cr => !ciFailures.Any(f =>
                string.Equals(f.Sha, sha, StringComparison.Ordinal) &&
                string.Equals(f.CheckRunName, cr.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (newFailures.Count == 0)
            return;

        foreach (var failedRun in newFailures)
        {
            if (ct.IsCancellationRequested) break;

            // Fetch job-level logs
            string? logContent = null;
            if (failedRun.Id > 0)
            {
                var rawLog = await gitHubApiProxy.FetchTextAsync(
                    token,
                    $"repos/{owner}/{repo}/actions/jobs/{failedRun.Id}/logs",
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(rawLog))
                    logContent = CiLogParser.ExtractRelevantLogLines(rawLog, MaxLogLines);
            }

            // Store failure details in metadata for user-initiated diagnosis
            ciFailures.Add(new CiFailure(
                sha,
                failedRun.Name,
                failedRun.Id,
                failedRun.Conclusion ?? string.Empty,
                failedRun.HtmlUrl ?? string.Empty,
                logContent,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        }

        // Persist updated failures in metadata
        SetCiFailures(metadata, ciFailures);
        await repository.UpdateMetadataAsync(link.Id, metadata.ToJsonString(), ct).ConfigureAwait(false);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "CiWatcherService started.")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "CiWatcherService stopped.")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error in CiWatcherService poll cycle.")]
    private partial void LogPollError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error processing CI status for smart link {LinkId}.")]
    private partial void LogLinkError(Exception ex, string linkId);

    internal sealed record CiFailure(
        string Sha,
        string CheckRunName,
        long CheckRunId,
        string Conclusion,
        string HtmlUrl,
        string? LogContent,
        string DetectedAt);
}
