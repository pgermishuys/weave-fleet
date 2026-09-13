using System.Globalization;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Progress;

/// <summary>
/// Works out a session's progress from its events. Pure: callers load and store the result.
/// Knows nothing about harnesses; it only sees Fleet's own events and the files they name.
/// </summary>
public static class SessionProgressTracker
{
    /// <summary>Event type pushed on the "sessions" topic with a <see cref="SessionProgressSummaryDto"/>.</summary>
    public const string SummaryEventType = "session_progress";

    /// <summary>Event type pushed on the session's own topic with a <see cref="SessionProgressDto"/>.</summary>
    public const string DetailEventType = "progress.updated";

    /// <summary>A markdown file needs at least this many unindented checkboxes to count as a plan.</summary>
    public const int MinimumPlanSteps = 3;

    /// <summary>How many plans a session remembers; the least recently written is dropped.</summary>
    public const int MaxPlans = 5;

    /// <summary>
    /// Applies a new todo list. Returns the new progress, or <see langword="null"/> when nothing changed
    /// (the same list again, or an empty list for a session that never had one).
    /// </summary>
    public static SessionProgress? ApplyTodos(
        SessionProgress? current,
        string sessionId,
        string userId,
        IReadOnlyList<TodoEntry> items,
        DateTimeOffset now)
    {
        if (current is null && items.Count == 0)
            return null;

        if (current is not null && current.Todos.SequenceEqual(items))
            return null;

        return Summarize(Base(current, sessionId, userId) with { Todos = [.. items], UpdatedAt = now });
    }

    /// <summary>
    /// Applies what a markdown file the session wrote now says. <paramref name="document"/> is null when the file is
    /// gone. A file with at least <see cref="MinimumPlanSteps"/> steps becomes a plan; boxes already ticked then
    /// count as done without a tick time. After that, each box that flips to ticked gets
    /// <paramref name="now"/> and <paramref name="messageId"/>. Returns <see langword="null"/> when nothing changed.
    /// </summary>
    public static SessionProgress? ApplyPlanFile(
        SessionProgress? current,
        string sessionId,
        string userId,
        string path,
        PlanDocument? document,
        string? messageId,
        DateTimeOffset now)
    {
        var plans = current?.Plans ?? [];
        var existing = plans.FirstOrDefault(plan => plan.Path == path);
        var steps = document?.Steps.Count() ?? 0;

        // A file that's gone, or no longer a checklist, stops being tracked.
        if (document is null || steps == 0 || (existing is null && steps < MinimumPlanSteps))
        {
            return existing is null
                ? null
                : Summarize(Base(current, sessionId, userId) with { Plans = [.. plans.Where(plan => plan != existing)], UpdatedAt = now });
        }

        var next = existing is null ? NewPlan(path, document, now) : UpdatePlan(existing, document, messageId, now);
        if (existing is not null && SameContent(existing, next))
            return null;

        var nextPlans = plans
            .Where(plan => plan.Path != path)
            .Prepend(next)
            .OrderByDescending(plan => plan.LastWrittenAt)
            .Take(MaxPlans)
            .ToList();

        return Summarize(Base(current, sessionId, userId) with { Plans = nextPlans, UpdatedAt = now });
    }

    /// <summary>The plan the counts come from: the one ticked most recently, else the one written most recently.</summary>
    public static TrackedPlan? ActivePlan(SessionProgress progress)
        => progress.Plans
            .OrderByDescending(plan => plan.LastTickedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(plan => plan.LastWrittenAt)
            .FirstOrDefault();

    public static SessionProgressSummaryDto ToSummary(SessionProgress progress)
        => new(progress.SessionId, progress.Kind, progress.Done, progress.Total, progress.Current);

    public static SessionProgressDto ToDto(SessionProgress progress)
        => new(
            progress.SessionId,
            progress.Kind,
            progress.Done,
            progress.Total,
            progress.Current,
            progress.Todos,
            Format(progress.UpdatedAt))
        {
            Plan = ActivePlan(progress) is { } plan ? ToDto(plan) : null,
        };

    private static SessionPlanDto ToDto(TrackedPlan plan)
        => new(
            plan.Path,
            plan.Title,
            Format(plan.TrackedSince),
            [.. plan.Groups.Select(group => new SessionPlanGroupDto(
                group.Title,
                [.. group.Steps.Select(step => new SessionPlanStepDto(
                    step.Key,
                    step.Number,
                    step.Title,
                    step.Checked,
                    step.SubDone,
                    step.SubTotal,
                    step.TickedAt is { } at ? Format(at) : null,
                    step.TickedInMessageId))]))]);

    private static SessionProgress Base(SessionProgress? current, string sessionId, string userId)
        => current ?? new SessionProgress { SessionId = sessionId, UserId = userId, Kind = SessionProgressKinds.Todos };

    /// <summary>Recomputes the counts: from the active plan when there is one, otherwise from the todo list.</summary>
    private static SessionProgress Summarize(SessionProgress progress)
    {
        if (ActivePlan(progress) is { } plan)
        {
            var steps = plan.Steps.ToList();
            return progress with
            {
                Kind = SessionProgressKinds.Plan,
                Done = steps.Count(step => step.Checked),
                Total = steps.Count,
                Current = steps.FirstOrDefault(step => !step.Checked)?.Title,
            };
        }

        var counted = progress.Todos.Where(item => item.Status != TodoStatuses.Cancelled).ToList();
        var currentItem = progress.Todos.FirstOrDefault(item => item.Status == TodoStatuses.InProgress)
                          ?? progress.Todos.FirstOrDefault(item => item.Status == TodoStatuses.Pending);
        return progress with
        {
            Kind = SessionProgressKinds.Todos,
            Done = counted.Count(item => item.Status == TodoStatuses.Completed),
            Total = counted.Count,
            Current = currentItem?.Content,
        };
    }

    private static TrackedPlan NewPlan(string path, PlanDocument document, DateTimeOffset now)
        => new()
        {
            Path = path,
            Title = document.Title,
            Groups = [.. document.Groups.Select(group => new TrackedPlanGroup
            {
                Title = group.Title,
                Steps = [.. group.Steps.Select(step => ToStep(step, tickedAt: null, messageId: null))],
            })],
            TrackedSince = now,
            LastWrittenAt = now,
        };

    private static TrackedPlan UpdatePlan(TrackedPlan existing, PlanDocument document, string? messageId, DateTimeOffset now)
    {
        var previous = existing.Steps
            .GroupBy(step => step.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ticked = false;

        TrackedPlanStep Next(PlanStep step)
        {
            if (!step.Checked)
                return ToStep(step, tickedAt: null, messageId: null);

            if (previous.TryGetValue(step.Key, out var before) && before.Checked)
                return ToStep(step, before.TickedAt, before.TickedInMessageId);

            ticked = true;
            return ToStep(step, now, messageId);
        }

        var groups = document.Groups
            .Select(group => new TrackedPlanGroup { Title = group.Title, Steps = [.. group.Steps.Select(Next)] })
            .ToList();

        return existing with
        {
            Title = document.Title,
            Groups = groups,
            LastWrittenAt = now,
            LastTickedAt = ticked ? now : existing.LastTickedAt,
        };
    }

    private static TrackedPlanStep ToStep(PlanStep step, DateTimeOffset? tickedAt, string? messageId)
        => new()
        {
            Key = step.Key,
            Number = step.Number,
            Title = step.Title,
            Checked = step.Checked,
            SubDone = step.SubDone,
            SubTotal = step.SubTotal,
            TickedAt = tickedAt,
            TickedInMessageId = messageId,
        };

    /// <summary>True when the plan reads the same: same title, headings, steps and ticks.</summary>
    private static bool SameContent(TrackedPlan a, TrackedPlan b)
        => a.Title == b.Title
           && a.Groups.Count == b.Groups.Count
           && a.Groups.Zip(b.Groups).All(pair =>
               pair.First.Title == pair.Second.Title && pair.First.Steps.SequenceEqual(pair.Second.Steps));

    private static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
