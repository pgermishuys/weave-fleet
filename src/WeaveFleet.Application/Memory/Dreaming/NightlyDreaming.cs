using System.Globalization;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory.Dreaming;

/// <summary>"Dream each night" in Settings → Memory, and what the last night did.</summary>
/// <param name="Enabled">The built-in automation exists.</param>
/// <param name="Paused">It exists but is switched off in Automations.</param>
/// <param name="Time">When it runs, <c>HH:mm</c> on <paramref name="TimeZone"/>'s clock.</param>
/// <param name="Model"><c>provider/model</c>; null for the harness's default.</param>
/// <param name="Recent">Dreams a night started in the last day, newest first: what the morning notice shows.</param>
public sealed record NightlyDreamingView(
    bool Enabled,
    string? AutomationId,
    bool Paused,
    string Time,
    string? TimeZone,
    string? HarnessType,
    string? Model,
    int BudgetTokens,
    string? NextRunAt,
    IReadOnlyList<DreamView> Recent);

/// <summary>What to change; a field left out keeps its value, and an empty <see cref="Model"/> means the harness's default.</summary>
public sealed record NightlyDreamingRequest(
    bool? Enabled,
    string? Time,
    string? TimeZone,
    string? HarnessType,
    string? Model,
    int? BudgetTokens);

/// <summary>
/// Turning on "Dream each night" creates the built-in automation Dreaming (03:00 on the user's clock, OpenCode 2); turning
/// it off removes it. Its time, harness and model live on the automation, and the budget in a preference.
/// </summary>
public sealed partial class NightlyDreaming(
    IAutomationRepository automations,
    IUserPreferenceRepository preferences,
    IDreamStore dreams,
    IUserContext user,
    TimeProvider time)
{
    public const string DefaultTime = "03:00";
    public const string DefaultHarness = "opencode2";
    public const int MinBudgetTokens = 10_000;
    public const int MaxBudgetTokens = 10_000_000;

    private static readonly TimeSpan RecentFor = TimeSpan.FromDays(1);

    public async Task<NightlyDreamingView> GetAsync(CancellationToken ct = default)
    {
        var automation = await automations.GetBuiltinAsync(BuiltinAutomations.Dreaming).ConfigureAwait(false);
        var budget = int.TryParse(await preferences.GetAsync(DreamRules.BudgetPreferenceKey).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored) && stored > 0
            ? stored
            : DreamRules.DefaultBudgetTokens;
        var now = time.GetUtcNow();
        var recent = await dreams.ListSinceAsync(user.UserId, now - RecentFor, DreamRules.ListLimit, ct).ConfigureAwait(false);
        return new NightlyDreamingView(
            automation is not null,
            automation?.Id,
            automation is { IsEnabled: false },
            automation is null ? DefaultTime : TimeOf(automation.TriggerConfig),
            automation?.TimeZone,
            automation?.HarnessType ?? DefaultHarness,
            automation?.Model,
            budget,
            automation is { IsEnabled: true } ? AutomationSchedule.NextOccurrenceUtc(automation, now.UtcDateTime)?.ToString("O") : null,
            [.. recent.Where(d => d.RunId is not null && !d.IsActive).Select(DreamView.Of)]);
    }

    public async Task<Result<NightlyDreamingView>> SetAsync(NightlyDreamingRequest request, CancellationToken ct = default)
    {
        var time24 = request.Time?.Trim();
        if (time24 is not null && ParseTime(time24) is null)
            return FleetError.ValidationError("Time", $"Pick a time like 03:00, not '{time24}'.");
        var zone = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone.Trim();
        if (zone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            return FleetError.ValidationError("TimeZone", $"Unknown time zone '{zone}'.");
        if (request.BudgetTokens is { } asked && (asked < MinBudgetTokens || asked > MaxBudgetTokens))
            return FleetError.ValidationError("BudgetTokens", $"A night's budget is between {MinBudgetTokens:N0} and {MaxBudgetTokens:N0} tokens.");
        if (!string.IsNullOrWhiteSpace(request.Model) && AutomationExecutionService.SplitModel(request.Model.Trim()).ModelId is null)
            return FleetError.ValidationError("Model", $"Model '{request.Model}' must be written provider/model.");

        if (request.BudgetTokens is { } budget)
            await preferences.SetAsync(DreamRules.BudgetPreferenceKey, budget.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

        var existing = await automations.GetBuiltinAsync(BuiltinAutomations.Dreaming).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime.ToString("O");
        if (request.Enabled == false)
        {
            if (existing is not null)
                await automations.DeleteAsync(existing.Id).ConfigureAwait(false);
            return await GetAsync(ct).ConfigureAwait(false);
        }

        if (existing is null)
        {
            if (request.Enabled != true)
                return await GetAsync(ct).ConfigureAwait(false);
            await automations.InsertAsync(new Automation
            {
                Id = Ulid.NewUlid().ToString(),
                Name = "Dreaming",
                Prompt = "",
                TriggerType = AutomationSchedule.ScheduleTrigger,
                TriggerConfig = CronOf(time24 ?? DefaultTime),
                MaxConcurrentRuns = 1,
                MaxRunsPerHour = 1,
                TimeoutMinutes = 120,
                IsEnabled = true,
                TargetType = BuiltinAutomations.Dreaming,
                TimeZone = zone,
                HarnessType = string.IsNullOrWhiteSpace(request.HarnessType) ? DefaultHarness : request.HarnessType.Trim(),
                Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
                Builtin = BuiltinAutomations.Dreaming,
                CreatedAt = now,
                UserId = user.UserId,
            }).ConfigureAwait(false);
            return await GetAsync(ct).ConfigureAwait(false);
        }

        if (time24 is not null)
            existing.TriggerConfig = CronOf(time24);
        if (zone is not null)
            existing.TimeZone = zone;
        if (!string.IsNullOrWhiteSpace(request.HarnessType))
            existing.HarnessType = request.HarnessType.Trim();
        if (request.Model is not null)
            existing.Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        // The schedule counts from here, so a new time doesn't catch up on the old one's nights.
        existing.UpdatedAt = now;
        await automations.UpdateAsync(existing).ConfigureAwait(false);
        return await GetAsync(ct).ConfigureAwait(false);
    }

    /// <summary><c>HH:mm</c> as hour and minute, or null.</summary>
    internal static (int Hour, int Minute)? ParseTime(string value)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? (at.Hour, at.Minute) : null;

    internal static string CronOf(string value)
        => ParseTime(value) is { } at ? string.Create(CultureInfo.InvariantCulture, $"{at.Minute} {at.Hour} * * *") : "0 3 * * *";

    /// <summary>The <c>HH:mm</c> of a daily cron (<c>0 3 * * *</c>); <see cref="DefaultTime"/> for anything else.</summary>
    internal static string TimeOf(string cron)
        => DailyCron().Match(cron.Trim()) is { Success: true } match
            ? string.Create(CultureInfo.InvariantCulture, $"{int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture):00}:{int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture):00}")
            : DefaultTime;

    [GeneratedRegex(@"^(\d{1,2}) (\d{1,2}) \* \* \*$")]
    private static partial Regex DailyCron();
}
