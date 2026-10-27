using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>Starts one dream (<see cref="DreamService.StartAsync"/>): what a night hands each repository to.</summary>
public interface IDreamStarter
{
    Task<Result<DreamView>> StartAsync(StartDreamRequest request, CancellationToken ct = default);
}

/// <summary>
/// Dreaming as a built-in automation: each night it starts one dream for every repository with sessions started since
/// the last night, one after another, and stops starting them once the night's token budget is spent. A night with
/// nothing new is skipped and costs nothing. The run's summary says what each repository's dream did.
/// </summary>
public sealed partial class DreamingAutomation(
    DreamingFeature feature,
    IUserPreferenceRepository preferences,
    IAutomationRunRepository runs,
    ISessionRepository sessions,
    IDreamStore dreams,
    IDreamStarter starter,
    IBackgroundUserScope users,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<DreamingAutomation> logger) : IBuiltinAutomations
{
    private static readonly TimeSpan FirstNightReadsBack = TimeSpan.FromDays(1);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>How often a night looks at whether its dream has ended.</summary>
    internal TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A dream still going after this no longer holds up the night.</summary>
    internal TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(90);

    public async Task<bool> IsOnAsync(Automation automation)
    {
        using var user = users.Begin(automation.UserId);
        return automation.Builtin == BuiltinAutomations.Dreaming && await feature.IsEnabledAsync().ConfigureAwait(false);
    }

    public async Task<AutomationExecutionOutcome> RunAsync(Automation automation, AutomationRun run, CancellationToken ct = default)
    {
        if (!await IsOnAsync(automation).ConfigureAwait(false))
            return AutomationExecutionOutcome.SkippedBecause("Skipped: Dreaming is off.");

        using var user = users.Begin(automation.UserId);
        var earlier = (await runs.ListByAutomationAsync(automation.Id, 20).ConfigureAwait(false))
            .Where(r => r.Id != run.Id && r.Status == AutomationRunStatus.Started && r.Summary is not null)
            .ToList();
        if (earlier.Any(r => r.SettledState is null))
            return AutomationExecutionOutcome.SkippedBecause("Skipped: the last night is still dreaming.");

        var since = earlier.Count > 0 && DateTimeOffset.TryParse(earlier[0].StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            ? last
            : time.GetUtcNow() - FirstNightReadsBack;
        var repositories = await RepositoriesSinceAsync(since).ConfigureAwait(false);
        if (repositories.Count == 0)
            return AutomationExecutionOutcome.SkippedBecause("Skipped: no new sessions since the last night, nothing to read.");

        var summary = $"Reading {string.Join(", ", repositories.Select(r => AgentMemory.RepositoryName(r.Repository)))}…";
        await runs.SetSummaryAsync(run.Id, summary).ConfigureAwait(false);

        var userId = automation.UserId;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var background = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
                await scope.ServiceProvider.GetRequiredService<DreamingAutomation>()
                    .NightAsync(automation, run.Id, since, repositories).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogNightFailed(ex, run.Id);
                await runs.SetSummaryAsync(run.Id, summary, AutomationRunState.Failed, "Fleet couldn't finish the night.").ConfigureAwait(false);
            }
        }, CancellationToken.None);

        return new AutomationExecutionOutcome(null, null, null, Summary: summary);
    }

    /// <summary>The repositories with sessions started since <paramref name="since"/>, the busiest first. Dreaming sessions don't count.</summary>
    internal async Task<List<(string Repository, int Sessions)>> RepositoriesSinceAsync(DateTimeOffset since)
    {
        var started = await sessions.ListCreatedSinceAsync(since.UtcDateTime.ToString("O"), DreamRules.NightSessionsScanned).ConfigureAwait(false);
        return [.. started
            .Where(s => !s.IsHidden && !s.Tags.Contains(DreamRules.SessionTag) && !string.IsNullOrWhiteSpace(s.Directory))
            .GroupBy(s => AgentMemory.RepositoryOf(s.Directory), PathComparer)
            .Where(group => Directory.Exists(group.Key))
            .Select(group => (Repository: group.Key, Sessions: group.Count()))
            .OrderByDescending(r => r.Sessions)
            .ThenBy(r => r.Repository, StringComparer.Ordinal)];
    }

    /// <summary>Dreams about each repository in turn until the budget is spent, writing the run's summary as it goes, then settles the run.</summary>
    internal async Task NightAsync(Automation automation, string runId, DateTimeOffset since, IReadOnlyList<(string Repository, int Sessions)> repositories)
    {
        var began = time.GetUtcNow();
        var budget = await BudgetAsync().ConfigureAwait(false);
        var (providerId, modelId) = AutomationExecutionService.SplitModel(automation.Model);
        var parts = new List<string>();
        long spent = 0;
        var done = 0;

        for (var i = 0; i < repositories.Count; i++)
        {
            var (repository, _) = repositories[i];
            var name = AgentMemory.RepositoryName(repository);
            if (spent >= budget)
            {
                parts.Add($"{name}: not read, the night's budget was spent");
                continue;
            }

            await runs.SetSummaryAsync(runId, string.Join(" · ", [.. parts, $"Dreaming about {name} ({i + 1} of {repositories.Count})…"])).ConfigureAwait(false);
            var started = await starter.StartAsync(new StartDreamRequest(repository, automation.HarnessType, providerId, modelId, since, runId)).ConfigureAwait(false);
            if (started.IsFailure)
            {
                parts.Add($"{name}: {started.Error.Description}");
                continue;
            }

            var dream = await WaitAsync(automation.UserId, started.Value.Id).ConfigureAwait(false);
            if (dream is null)
            {
                parts.Add($"{name}: still dreaming after {GiveUpAfter.TotalMinutes:0} min");
                continue;
            }

            if (await TokensOfAsync(dream.SessionId).ConfigureAwait(false) is { } tokens)
            {
                dream.Tokens = tokens;
                await dreams.SaveAsync(automation.UserId, dream).ConfigureAwait(false);
                spent += tokens;
            }

            if (dream.Status == DreamRules.Statuses.Done)
                done++;
            parts.Add(Describe(name, dream));
        }

        var minutes = Math.Max(1, (int)Math.Round((time.GetUtcNow() - began).TotalMinutes));
        parts.Add($"{minutes} min, {FormatTokens(spent)} tokens");
        await runs.SetSummaryAsync(
            runId,
            string.Join(" · ", parts),
            done > 0 ? AutomationRunState.Done : AutomationRunState.Failed).ConfigureAwait(false);
    }

    private async Task<Dream?> WaitAsync(string userId, string dreamId)
    {
        var giveUp = time.GetUtcNow() + GiveUpAfter;
        while (true)
        {
            var dream = await dreams.FindAsync(userId, dreamId).ConfigureAwait(false);
            if (dream is null || !dream.IsActive)
                return dream;
            if (time.GetUtcNow() >= giveUp)
                return null;
            await Task.Delay(PollEvery, time).ConfigureAwait(false);
        }
    }

    /// <summary>What the dreaming session cost. Usage is counted as it arrives, so a total still at 0 is read once more.</summary>
    private async Task<int?> TokensOfAsync(string? sessionId)
    {
        if (sessionId is null || await sessions.GetByIdAsync(sessionId).ConfigureAwait(false) is not { } session)
            return null;
        if (session.TotalTokens > 0)
            return session.TotalTokens;
        await Task.Delay(PollEvery, time).ConfigureAwait(false);
        return (await sessions.GetByIdAsync(sessionId).ConfigureAwait(false))?.TotalTokens ?? 0;
    }

    private async Task<int> BudgetAsync()
        => int.TryParse(await preferences.GetAsync(DreamRules.BudgetPreferenceKey).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) && budget > 0
            ? budget
            : DreamRules.DefaultBudgetTokens;

    /// <summary>"orchard: 2 notes changed", "orchard: 1 note added", "orchard: no changes", or why it failed.</summary>
    internal static string Describe(string name, Dream dream)
    {
        if (dream.Status != DreamRules.Statuses.Done)
            return $"{name}: {(dream.Error ?? "failed").TrimEnd('.')}";

        var applied = dream.Changes.Where(c => c.Kind is not (DreamRules.ChangeKinds.Left or DreamRules.ChangeKinds.Skipped)).ToList();
        return applied.Count switch
        {
            0 => $"{name}: no changes",
            1 => $"{name}: 1 note {applied[0].Kind}",
            var count => $"{name}: {count} notes changed",
        };
    }

    internal static string FormatTokens(long tokens)
        => tokens >= 1_000_000 ? $"{tokens / 1_000_000d:0.#}M".Replace(".0M", "M", StringComparison.Ordinal)
            : tokens >= 1_000 ? $"{Math.Round(tokens / 1_000d):0}k"
            : tokens.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The dreaming night of run {RunId} failed")]
    private partial void LogNightFailed(Exception ex, string runId);
}
