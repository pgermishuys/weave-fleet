using System.Globalization;
using Cronos;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Services;

/// <summary>What the scheduler should do about an automation's latest occurrence.</summary>
public enum ScheduleDecisionKind
{
    /// <summary>It's due now: run it.</summary>
    OnTime,
    /// <summary>It was missed, but by less than <see cref="AutomationSchedule.CatchUpLimit"/>: run it once, late.</summary>
    CatchUp,
    /// <summary>It was missed by more than that: record it as skipped.</summary>
    Missed,
}

public sealed record ScheduleDecision(ScheduleDecisionKind Kind, DateTime OccurrenceUtc);

/// <summary>
/// When scheduled automations run. A "schedule" trigger is a cron expression and a "once" trigger a local date and
/// time (<c>2026-09-21T09:00</c>); both are read in the automation's IANA time zone, and no zone means UTC.
/// </summary>
public static class AutomationSchedule
{
    public const string ScheduleTrigger = "schedule";
    public const string OnceTrigger = "once";
    public const string OnceFormat = "yyyy-MM-dd'T'HH:mm";

    /// <summary>How late a run can be and still count as on time: the scheduler polls every 30 seconds.</summary>
    public static readonly TimeSpan OnTimeSlack = TimeSpan.FromMinutes(2);

    /// <summary>A run missed while Fleet was off still runs if it's less late than this; otherwise it's skipped.</summary>
    public static readonly TimeSpan CatchUpLimit = TimeSpan.FromHours(3);

    public static bool IsTimed(string triggerType) =>
        triggerType.Equals(ScheduleTrigger, StringComparison.OrdinalIgnoreCase)
        || triggerType.Equals(OnceTrigger, StringComparison.OrdinalIgnoreCase);

    public static bool IsOnce(string triggerType) => triggerType.Equals(OnceTrigger, StringComparison.OrdinalIgnoreCase);

    /// <summary>The automation's zone; an unknown or missing one is UTC.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZone) =>
        !string.IsNullOrWhiteSpace(timeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out var zone)
            ? zone
            : TimeZoneInfo.Utc;

    /// <summary>A "once" trigger's local date and time, or null when it isn't one.</summary>
    public static DateTime? ParseOnce(string triggerConfig) =>
        DateTime.TryParseExact(triggerConfig.Trim(), OnceFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;

    /// <summary>A "once" trigger's moment in UTC, or null when the config isn't a date and time.</summary>
    public static DateTime? OnceUtc(string triggerConfig, string? timeZone)
    {
        var local = ParseOnce(triggerConfig);
        if (local is null)
            return null;

        var zone = ResolveTimeZone(timeZone);
        // A time skipped by a clock change runs an hour later, as cron does.
        var value = zone.IsInvalidTime(local.Value) ? local.Value.AddHours(1) : local.Value;
        return TimeZoneInfo.ConvertTimeToUtc(value, zone);
    }

    /// <summary>The first run at or after <paramref name="fromUtc"/>, in UTC; null when there isn't one.</summary>
    public static DateTime? NextOccurrenceUtc(Automation automation, DateTime fromUtc)
    {
        if (IsOnce(automation.TriggerType))
        {
            var at = OnceUtc(automation.TriggerConfig, automation.TimeZone);
            return at >= fromUtc ? at : null;
        }

        if (!automation.TriggerType.Equals(ScheduleTrigger, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return CronExpression.Parse(automation.TriggerConfig)
                .GetNextOccurrence(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), ResolveTimeZone(automation.TimeZone), inclusive: true);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    /// <summary>The latest run after <paramref name="afterUtc"/> and no later than <paramref name="untilUtc"/>.</summary>
    public static DateTime? LatestOccurrenceUtc(Automation automation, DateTime afterUtc, DateTime untilUtc)
    {
        if (untilUtc <= afterUtc)
            return null;

        if (IsOnce(automation.TriggerType))
        {
            var at = OnceUtc(automation.TriggerConfig, automation.TimeZone);
            return at > afterUtc && at <= untilUtc ? at : null;
        }

        if (!automation.TriggerType.Equals(ScheduleTrigger, StringComparison.OrdinalIgnoreCase))
            return null;

        CronExpression cron;
        try
        {
            cron = CronExpression.Parse(automation.TriggerConfig);
        }
        catch (CronFormatException)
        {
            return null;
        }

        // Every-minute crons over a long sleep have many occurrences; only the last one matters.
        DateTime? latest = null;
        foreach (var occurrence in cron.GetOccurrences(
                     DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc),
                     DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc),
                     ResolveTimeZone(automation.TimeZone),
                     fromInclusive: false,
                     toInclusive: true))
        {
            latest = occurrence;
        }

        return latest;
    }

    /// <summary>
    /// What to do about the latest occurrence since <paramref name="handledUntilUtc"/> (the last one recorded, or when
    /// the automation was switched on or changed). Null when nothing is due.
    /// </summary>
    public static ScheduleDecision? Decide(Automation automation, DateTime handledUntilUtc, DateTime nowUtc)
    {
        var occurrence = LatestOccurrenceUtc(automation, handledUntilUtc, nowUtc);
        if (occurrence is null)
            return null;

        var late = nowUtc - occurrence.Value;
        var kind = late <= OnTimeSlack ? ScheduleDecisionKind.OnTime
            : late <= CatchUpLimit ? ScheduleDecisionKind.CatchUp
            : ScheduleDecisionKind.Missed;
        return new ScheduleDecision(kind, occurrence.Value);
    }

    /// <summary>Why a missed run was skipped, with its time on the automation's clock.</summary>
    public static string DescribeMissed(Automation automation, DateTime occurrenceUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occurrenceUtc, DateTimeKind.Utc), ResolveTimeZone(automation.TimeZone));
        return $"Skipped: Fleet wasn't running at {local.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture)}, and it was more than {CatchUpLimit.TotalHours:0} hours late when Fleet started.";
    }

    /// <summary>
    /// Checks a timed trigger: a cron expression that parses, or a date and time; and a zone Fleet knows.
    /// <paramref name="requireFuture"/> refuses a "once" time that has passed, for an automation that would be on.
    /// </summary>
    public static FleetError? Validate(string triggerType, string triggerConfig, string? timeZone, DateTime nowUtc, bool requireFuture)
    {
        if (!IsTimed(triggerType))
            return null;

        var zone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        if (zone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            return FleetError.ValidationError(
                "TimeZone",
                $"Unknown time zone '{zone}'. Use an IANA name such as 'Europe/London'.");
        }

        if (IsOnce(triggerType))
        {
            var at = OnceUtc(triggerConfig, zone);
            if (at is null)
            {
                return FleetError.ValidationError(
                    "TriggerConfig",
                    $"A one-off run needs a date and time like 2026-09-21T09:00, not '{triggerConfig}'.");
            }

            if (requireFuture && at <= nowUtc)
                return FleetError.ValidationError("TriggerConfig", "That time has already passed. Pick a later one.");

            return null;
        }

        try
        {
            CronExpression.Parse(triggerConfig);
        }
        catch (Exception ex)
        {
            return FleetError.ValidationError(
                "TriggerConfig",
                $"Invalid cron expression: {ex.Message}");
        }

        return null;
    }
}
