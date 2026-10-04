using System.Text.Json;
using WeaveFleet.Application;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Progress;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// The one writer of a session's running work: subagents (delegations to a child session), background shells,
/// monitors and other tasks. Harnesses report work as <see cref="EventTypes.WorkStarted"/>,
/// <see cref="EventTypes.WorkUpdated"/> and <see cref="EventTypes.WorkEnded"/> events, which the relay hands here; this
/// saves them and tells clients with <c>work.*</c> events (and, for subagents, the <c>delegation.*</c> events the
/// conversation's tool cards read).
/// </summary>
public sealed class DelegationService(
    IDelegationRepository delegationRepository,
    IEventBroadcaster eventBroadcaster,
    IUserContext userContext,
    SessionActivityWriteService? sessionActivityWriteService,
    SessionActivityTracker? activityTracker,
    ISessionRepository? sessionRepository,
    SessionCapabilitiesResolver? capabilitiesResolver,
    ISessionProgressObserver? progressObserver = null)
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        "completed",
        "error",
        "cancelled"
    };

    public DelegationService(
        IDelegationRepository delegationRepository,
        IEventBroadcaster eventBroadcaster,
        IUserContext userContext)
        : this(
            delegationRepository,
            eventBroadcaster,
            userContext,
            sessionActivityWriteService: null,
            activityTracker: null,
            sessionRepository: null,
            capabilitiesResolver: null)
    {
    }

    public DelegationService(
        IDelegationRepository delegationRepository,
        IEventBroadcaster eventBroadcaster,
        IUserContext userContext,
        SessionActivityWriteService? sessionActivityWriteService,
        SessionActivityTracker? activityTracker)
        : this(
            delegationRepository,
            eventBroadcaster,
            userContext,
            sessionActivityWriteService,
            activityTracker,
            sessionRepository: null,
            capabilitiesResolver: null)
    {
    }

    /// <param name="description">What the subagent was asked to do, when the harness says: its label, and passed on to progress.</param>
    /// <param name="workId">The harness's handle for the subagent, when it isn't the call's id.</param>
    public async Task<DelegationDto> HandleDelegationDetectedAsync(
        string parentSessionId,
        string parentToolCallId,
        string title,
        string? description = null,
        string? workId = null)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ValidateRequired(parentToolCallId, nameof(parentToolCallId));
        ValidateRequired(title, nameof(title));

        var existing = await delegationRepository.GetByParentToolCallIdAsync(parentSessionId, parentToolCallId);
        if (existing is not null)
            return ToDto(existing);

        var now = DateTime.UtcNow.ToString("O");
        var delegation = new Delegation
        {
            Id = Guid.NewGuid().ToString(),
            ParentSessionId = parentSessionId,
            ParentToolCallId = parentToolCallId,
            Title = title,
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now,
            Kind = WorkKinds.Subagent,
            WorkId = workId ?? parentToolCallId,
            Label = description,
        };

        if (sessionActivityWriteService is null)
        {
            await delegationRepository.InsertAsync(delegation);
            await BroadcastAsync(parentSessionId, "delegation.created", delegation);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationsToInsert = [delegation],
                    OutboxMessages = [CreateOutboxMessage(parentSessionId, "delegation.created", delegation, now)]
                },
                CancellationToken.None);
        }

        ObserveForProgress(delegation, new DelegationCreated { Payload = CreatedPayload(delegation) with { Description = description } });
        return ToDto(delegation);
    }

    public async Task<DelegationDto?> HandleChildLinkedAsync(
        string parentSessionId,
        string parentToolCallId,
        string childSessionId)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ValidateRequired(parentToolCallId, nameof(parentToolCallId));
        ValidateRequired(childSessionId, nameof(childSessionId));

        var delegation = await delegationRepository.GetByParentToolCallIdAsync(parentSessionId, parentToolCallId);
        if (delegation is null)
            return null;

        if (delegation.Status != "pending" && delegation.Status != "running")
            throw new InvalidOperationException($"Cannot link child session when delegation is '{delegation.Status}'.");

        var shouldUpdateChild = !string.Equals(delegation.ChildSessionId, childSessionId, StringComparison.Ordinal);
        var shouldUpdateStatus = delegation.Status != "running";

        if (!shouldUpdateChild && !shouldUpdateStatus)
            return ToDto(delegation);

        var now = DateTime.UtcNow.ToString("O");
        delegation.ChildSessionId = childSessionId;
        delegation.Status = "running";
        delegation.UpdatedAt = now;
        delegation.CompletedAt = null;

        if (sessionActivityWriteService is null)
        {
            if (shouldUpdateChild)
                await delegationRepository.UpdateChildSessionIdAsync(delegation.Id, childSessionId, now);

            if (shouldUpdateStatus)
                await delegationRepository.UpdateStatusAsync(delegation.Id, "running", now, null);

            await BroadcastAsync(parentSessionId, "delegation.updated", delegation);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationChildSessionUpdates = shouldUpdateChild
                        ? [new DelegationChildSessionUpdate
                        {
                            Id = delegation.Id,
                            ChildSessionId = childSessionId,
                            UpdatedAt = now
                        }]
                        : [],
                    DelegationStatusUpdates = shouldUpdateStatus
                        ? [new DelegationStatusUpdate
                        {
                            Id = delegation.Id,
                            Status = "running",
                            UpdatedAt = now,
                            CompletedAt = null
                        }]
                        : [],
                    OutboxMessages = [CreateOutboxMessage(parentSessionId, "delegation.updated", delegation, now)]
                },
                CancellationToken.None);
        }

        if (shouldUpdateChild)
            activityTracker?.RegisterChild(childSessionId, parentSessionId);

        ObserveForProgress(delegation, new DelegationUpdated { Payload = UpdatedPayload(delegation) });
        return ToDto(delegation);
    }

    /// <summary>
    /// Records that the call which started a delegation returned while its child carries on working: the work went
    /// into the background (OpenCode 2's <c>subagent</c> with <c>background: true</c>, or a call moved there later).
    /// The parent is free for the user's next prompt from then on, so the child's work no longer makes it read as
    /// busy anywhere, while a question from the child still makes it wait on the user. The delegation stays running
    /// until the harness says the child finished.
    /// </summary>
    /// <returns>The delegation, or <c>null</c> when there's none for that call.</returns>
    public async Task<DelegationDto?> HandleDelegationMovedToBackgroundAsync(string parentSessionId, string parentToolCallId)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ValidateRequired(parentToolCallId, nameof(parentToolCallId));

        var delegation = await delegationRepository.GetByParentToolCallIdAsync(parentSessionId, parentToolCallId);
        if (delegation is null)
            return null;

        // Only a delegation still under way whose child Fleet knows can be in the background; one moved there already
        // needs nothing more.
        if (delegation.Status is not ("pending" or "running")
            || delegation.ChildSessionId is not { } childSessionId
            || activityTracker is null
            || !activityTracker.MoveChildToBackground(childSessionId))
        {
            return ToDto(delegation);
        }

        await BroadcastAsync(parentSessionId, "delegation.updated", delegation);

        // The parent may already have reported itself idle while its child counted as its work.
        await BroadcastParentActivityAsync(parentSessionId).ConfigureAwait(false);
        return ToDto(delegation);
    }

    public async Task<DelegationDto?> HandleDelegationFinishedAsync(string delegationId, string status)
    {
        ValidateRequired(delegationId, nameof(delegationId));
        ValidateTerminalStatus(status);

        var delegation = await delegationRepository.GetByIdAsync(delegationId);
        if (delegation is null)
            return null;

        if (TerminalStatuses.Contains(delegation.Status))
        {
            if (delegation.Status == status)
                return ToDto(delegation);

            throw new InvalidOperationException($"Cannot transition terminal delegation from '{delegation.Status}' to '{status}'.");
        }

        if (delegation.Status != "pending" && delegation.Status != "running")
            throw new InvalidOperationException($"Cannot finish delegation from '{delegation.Status}'.");

        var now = DateTime.UtcNow.ToString("O");
        delegation.Status = status;
        delegation.UpdatedAt = now;
        delegation.CompletedAt = now;

        if (sessionActivityWriteService is null)
        {
            await delegationRepository.UpdateStatusAsync(delegation.Id, status, now, now);
            await BroadcastAsync(delegation.ParentSessionId, "delegation.updated", delegation);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationStatusUpdates = [new DelegationStatusUpdate
                    {
                        Id = delegation.Id,
                        Status = status,
                        UpdatedAt = now,
                        CompletedAt = now
                    }],
                    OutboxMessages = [CreateOutboxMessage(delegation.ParentSessionId, "delegation.updated", delegation, now)]
                },
                CancellationToken.None);
        }

        ObserveForProgress(delegation, new DelegationCompleted { Payload = CompletedPayload(delegation) });

        if (delegation.ChildSessionId is not null && activityTracker is not null)
        {
            activityTracker.UnregisterChild(delegation.ChildSessionId);
            await BroadcastParentActivityAsync(delegation.ParentSessionId).ConfigureAwait(false);
        }

        return ToDto(delegation);
    }

    /// <summary>Tells the parent's conversation and the session list what the parent shows now that its children changed.</summary>
    private async Task BroadcastParentActivityAsync(string parentSessionId)
    {
        if (activityTracker is null)
            return;

        var parentActivityStatus = activityTracker.GetEffectiveActivityStatus(parentSessionId) ?? "idle";
        var activityPayload = await BuildActivityStatusPayloadAsync(
            parentSessionId,
            parentActivityStatus).ConfigureAwait(false);

        await eventBroadcaster.BroadcastAsync(
            $"session:{parentSessionId}",
            "activity_status",
            activityPayload,
            userContext.UserId,
            CancellationToken.None).ConfigureAwait(false);

        await eventBroadcaster.BroadcastAsync(
            "sessions",
            "activity_status",
            activityPayload,
            userContext.UserId,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Hands the delegation to progress tracking, which shows subagents under the plan step they work on.</summary>
    private void ObserveForProgress(Delegation delegation, DomainEvent domainEvent)
        => progressObserver?.Observe(delegation.ParentSessionId, userContext.UserId, domainEvent);

    private static DelegationCreatedPayload CreatedPayload(Delegation delegation) => new()
    {
        DelegationId = delegation.Id,
        ParentSessionId = delegation.ParentSessionId,
        ParentToolCallId = delegation.ParentToolCallId,
        ChildSessionId = delegation.ChildSessionId,
        Title = delegation.Title,
        Status = delegation.Status,
        CreatedAt = delegation.CreatedAt,
    };

    private static DelegationUpdatedPayload UpdatedPayload(Delegation delegation) => new()
    {
        DelegationId = delegation.Id,
        ParentSessionId = delegation.ParentSessionId,
        ParentToolCallId = delegation.ParentToolCallId,
        ChildSessionId = delegation.ChildSessionId,
        Title = delegation.Title,
        Status = delegation.Status,
        CreatedAt = delegation.CreatedAt,
    };

    private static DelegationCompletedPayload CompletedPayload(Delegation delegation) => new()
    {
        DelegationId = delegation.Id,
        ParentSessionId = delegation.ParentSessionId,
        ParentToolCallId = delegation.ParentToolCallId,
        ChildSessionId = delegation.ChildSessionId,
        Title = delegation.Title,
        Status = delegation.Status,
        CreatedAt = delegation.CreatedAt,
        CompletedAt = delegation.CompletedAt ?? delegation.UpdatedAt,
    };

    private async Task<JsonElement> BuildActivityStatusPayloadAsync(string sessionId, string activityStatus)
    {
        var session = sessionRepository is not null
            ? await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false)
            : null;
        if (session is not null)
        {
            session.ActivityStatus = activityStatus;
        }

        var capabilities = session is not null && capabilitiesResolver is not null
            ? capabilitiesResolver.Resolve(session)
            : SessionCapabilitiesResolver.Resolve(null, null, activityStatus, isLive: false);

        return JsonSerializer.SerializeToElement(
            new ActivityStatusBroadcastPayload(sessionId, activityStatus, capabilities),
            ApplicationJsonContext.Default.ActivityStatusBroadcastPayload);
    }

    public async Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(string parentSessionId)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));

        var delegations = await delegationRepository.GetByParentSessionIdAsync(parentSessionId);
        return delegations.Where(d => d.Kind == WorkKinds.Subagent).Select(ToDto).ToList();
    }

    /// <summary>How long work that ended still shows with the running work, with its result.</summary>
    public static readonly TimeSpan RecentlyEndedFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Records what a harness said about a piece of running work (<see cref="EventTypes.WorkStarted"/> or
    /// <see cref="EventTypes.WorkUpdated"/>): the first report of a <see cref="WorkReport.WorkId"/> starts it, a later one
    /// changes only what it sets. A subagent is a delegation as well, so its tool card links to its child session and
    /// its parent counts it as working until it goes to the background. Work that ended stays as it ended.
    /// </summary>
    /// <param name="childSessionId">The Fleet session the work runs in, made from <see cref="WorkReport.ChildHarnessSessionId"/>.</param>
    /// <returns>The work as Fleet shows it now.</returns>
    public async Task<RunningWorkItem> HandleWorkReportedAsync(string parentSessionId, WorkReport report, string? childSessionId = null)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ArgumentNullException.ThrowIfNull(report);
        ValidateRequired(report.WorkId, nameof(report));

        var work = await delegationRepository.GetByWorkIdAsync(parentSessionId, report.WorkId).ConfigureAwait(false);
        if (work is { IsRunning: false })
            return ToItem(work);

        var started = work is null;
        if (work is null)
        {
            var kind = WorkKinds.IsKnown(report.Kind) ? report.Kind! : WorkKinds.Task;
            var title = string.IsNullOrWhiteSpace(report.Title) ? kind : report.Title;
            // A subagent that is its call's own work (named by the call, or running in a child session) is the call's
            // delegation. One the harness names apart from its call, with no session of its own (Pi's subagent
            // extension runs several in one call), is a record of its own: a delegation is one per call.
            if (kind == WorkKinds.Subagent
                && report.ToolCallId is { Length: > 0 } callId
                && (report.WorkId == callId || report.ChildHarnessSessionId is not null))
            {
                await HandleDelegationDetectedAsync(parentSessionId, callId, title, report.Label, report.WorkId).ConfigureAwait(false);
                work = await delegationRepository.GetByWorkIdAsync(parentSessionId, report.WorkId).ConfigureAwait(false)
                    ?? await delegationRepository.GetByParentToolCallIdAsync(parentSessionId, callId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Session '{parentSessionId}' has no subagent call '{callId}'.");
            }
            else
            {
                var now = DateTime.UtcNow.ToString("O");
                work = new Delegation
                {
                    Id = Guid.NewGuid().ToString(),
                    ParentSessionId = parentSessionId,
                    ParentToolCallId = report.ToolCallId,
                    Title = title,
                    Status = "running",
                    CreatedAt = now,
                    UpdatedAt = now,
                    Kind = kind,
                    WorkId = report.WorkId,
                    Label = report.Label,
                };
                await delegationRepository.InsertAsync(work).ConfigureAwait(false);
            }
        }

        var changed = Merge(work, report);

        if (childSessionId is not null && !string.Equals(work.ChildSessionId, childSessionId, StringComparison.Ordinal))
        {
            if (work.Kind == WorkKinds.Subagent && work.ParentToolCallId is { } callId)
            {
                await HandleChildLinkedAsync(parentSessionId, callId, childSessionId).ConfigureAwait(false);
            }
            else
            {
                var now = DateTime.UtcNow.ToString("O");
                await delegationRepository.UpdateChildSessionIdAsync(work.Id, childSessionId, now).ConfigureAwait(false);
                if (work.Status != "running")
                    await delegationRepository.UpdateStatusAsync(work.Id, "running", now, null).ConfigureAwait(false);
            }

            work.ChildSessionId = childSessionId;
            work.Status = "running";
            changed = true;
        }

        // The call returned while its child works on: the parent is free, and the child's work isn't its own.
        if (work.Background && work.Kind == WorkKinds.Subagent && work.ChildSessionId is not null && work.ParentToolCallId is { } backgroundCallId)
            await HandleDelegationMovedToBackgroundAsync(parentSessionId, backgroundCallId).ConfigureAwait(false);

        if (changed || started)
        {
            work.UpdatedAt = DateTime.UtcNow.ToString("O");
            await delegationRepository.UpdateWorkAsync(work).ConfigureAwait(false);
            await BroadcastWorkAsync(started ? EventTypes.WorkStarted : EventTypes.WorkUpdated, work).ConfigureAwait(false);
        }

        return ToItem(work);
    }

    /// <summary>
    /// Records that a piece of running work ended (<see cref="EventTypes.WorkEnded"/>), with how
    /// (<see cref="WorkEndedReasons"/>; completed when the harness doesn't say). Work that ended already stays as it
    /// ended: a stop Fleet recorded at once isn't undone by the harness's own report coming after.
    /// </summary>
    /// <returns>The work as Fleet shows it now, or <c>null</c> when Fleet has no such work.</returns>
    public async Task<RunningWorkItem?> HandleWorkEndedAsync(string parentSessionId, string workId, string? endedReason, string? detail = null)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ValidateRequired(workId, nameof(workId));

        var work = await delegationRepository.GetByWorkIdAsync(parentSessionId, workId).ConfigureAwait(false);
        if (work is null)
            return null;
        if (!work.IsRunning)
            return ToItem(work);

        var reason = WorkEndedReasons.IsKnown(endedReason) ? endedReason! : WorkEndedReasons.Completed;
        var status = reason == WorkEndedReasons.Lost ? WorkEndedReasons.Cancelled : reason;

        // A subagent's delegation ends the way it always has: its card, its child's activity and progress hear of it.
        if (work.Kind == WorkKinds.Subagent)
            await HandleDelegationFinishedAsync(work.Id, status).ConfigureAwait(false);

        var now = DateTime.UtcNow.ToString("O");
        await delegationRepository.EndAsync(work.Id, status, reason, detail, now).ConfigureAwait(false);
        work.Status = status;
        work.EndedReason = reason;
        work.Detail = detail ?? work.Detail;
        work.UpdatedAt = now;
        work.CompletedAt = now;

        await BroadcastWorkAsync(EventTypes.WorkEnded, work).ConfigureAwait(false);
        return ToItem(work);
    }

    /// <summary>
    /// Catches up with what the harness itself says is running in the session (<see cref="IHarnessSession.GetRunningWorkAsync"/>):
    /// work Fleet still has running that isn't in <paramref name="running"/> ended with the harness, so it ends lost
    /// (<see cref="WorkEndedReasons.Lost"/>). A report matches by <see cref="WorkReport.WorkId"/>, or by the child
    /// session the work runs in (<see cref="WorkReport.ChildHarnessSessionId"/>), for a harness that can say which
    /// children run but no longer knows which call started them.
    /// </summary>
    /// <returns>How much work ended as lost.</returns>
    public async Task<int> SettleLostWorkAsync(string parentSessionId, IReadOnlyList<WorkReport> running)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        ArgumentNullException.ThrowIfNull(running);

        var workIds = running.Select(r => r.WorkId).ToHashSet(StringComparer.Ordinal);
        var children = running.Select(r => r.ChildHarnessSessionId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var lost = 0;
        foreach (var work in await delegationRepository.GetByParentSessionIdAsync(parentSessionId).ConfigureAwait(false))
        {
            if (!work.IsRunning || workIds.Contains(work.WorkId) || await RunsInAsync(work, children).ConfigureAwait(false))
                continue;

            await HandleWorkEndedAsync(parentSessionId, work.WorkId, WorkEndedReasons.Lost).ConfigureAwait(false);
            lost++;
        }

        return lost;
    }

    /// <summary>Whether <paramref name="work"/>'s child session is one of the harness's <paramref name="childHarnessSessionIds"/>.</summary>
    private async Task<bool> RunsInAsync(Delegation work, HashSet<string> childHarnessSessionIds)
    {
        if (childHarnessSessionIds.Count == 0 || work.ChildSessionId is not { } childSessionId || sessionRepository is null)
            return false;

        var child = await sessionRepository.GetByIdAsync(childSessionId).ConfigureAwait(false);
        return child is not null
            && (childHarnessSessionIds.Contains(child.OpencodeSessionId)
                || (child.HarnessResumeToken is { } token && childHarnessSessionIds.Contains(token)));
    }

    /// <summary>
    /// The work of session <paramref name="parentSessionId"/> that was lost and its agent hasn't been told about, oldest
    /// first. One query on an index of only such rows, so a session with none pays nothing for it.
    /// </summary>
    public Task<IReadOnlyList<Delegation>> GetUnreportedLostWorkAsync(string parentSessionId)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));
        return delegationRepository.GetUnreportedLostAsync(parentSessionId);
    }

    /// <summary>Records that the agent was told <paramref name="lost"/> ended lost (<see cref="LostWorkNote"/>), so it isn't told again.</summary>
    public Task MarkLostWorkReportedAsync(IReadOnlyList<Delegation> lost)
        => delegationRepository.MarkLostReportedAsync([.. lost.Select(work => work.Id)], DateTime.UtcNow.ToString("O"));

    /// <summary>
    /// The session's running work, and the work that ended in the last <see cref="RecentlyEndedFor"/> with its result;
    /// every piece it ever ran with <paramref name="all"/>. Oldest first.
    /// </summary>
    public async Task<IReadOnlyList<RunningWorkItem>> GetWorkAsync(string parentSessionId, bool all = false)
    {
        ValidateRequired(parentSessionId, nameof(parentSessionId));

        var work = await delegationRepository.GetByParentSessionIdAsync(parentSessionId).ConfigureAwait(false);
        return all ? work.Select(ToItem).ToList() : RunningWorkOf(work);
    }

    /// <summary>
    /// Of a session's work, what shows as its running work: what still runs, and what ended in the last
    /// <see cref="RecentlyEndedFor"/>, oldest first. The session snapshot's <c>runningWork</c>.
    /// </summary>
    public static IReadOnlyList<RunningWorkItem> RunningWorkOf(IEnumerable<Delegation> work)
    {
        var since = DateTime.UtcNow - RecentlyEndedFor;
        return work
            .Where(item => item.IsRunning || EndedSince(item, since))
            .OrderBy(item => item.CreatedAt, StringComparer.Ordinal)
            .Select(ToItem)
            .ToList();
    }

    /// <summary>The current user's running work in every session, oldest first: what the status bar counts.</summary>
    public async Task<IReadOnlyList<RunningWorkItem>> GetAllRunningWorkAsync()
    {
        var work = await delegationRepository.ListRunningAsync().ConfigureAwait(false);
        return work.Select(ToItem).ToList();
    }

    /// <summary>A running work item of session <paramref name="parentSessionId"/> by Fleet's id, or <c>null</c>.</summary>
    public async Task<Delegation?> FindWorkAsync(string parentSessionId, string id)
    {
        var work = await delegationRepository.GetByIdAsync(id).ConfigureAwait(false);
        return work is not null && string.Equals(work.ParentSessionId, parentSessionId, StringComparison.Ordinal) ? work : null;
    }

    /// <summary>Fleet's record of a piece of work as clients see it.</summary>
    public static RunningWorkItem ToItem(Delegation work) => new()
    {
        Id = work.Id,
        SessionId = work.ParentSessionId,
        WorkId = string.IsNullOrEmpty(work.WorkId) ? work.ParentToolCallId ?? work.Id : work.WorkId,
        Kind = work.Kind,
        Title = work.Title,
        Label = work.Label,
        Status = work.Status,
        Background = work.Background,
        ChildSessionId = work.ChildSessionId,
        ToolCallId = work.ParentToolCallId,
        CanStop = work.CanStop,
        CanReadOutput = work.CanReadOutput,
        StartedAt = work.CreatedAt,
        EndedAt = work.IsRunning ? null : work.CompletedAt ?? work.UpdatedAt,
        EndedReason = work.IsRunning ? null : work.EndedReason ?? work.Status,
        Detail = work.Detail,
    };

    private static bool EndedSince(Delegation work, DateTime since)
        => DateTime.TryParse(work.CompletedAt ?? work.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ended)
            && ended.ToUniversalTime() >= since;

    /// <summary>Applies what <paramref name="report"/> sets to <paramref name="work"/>; true when anything changed.</summary>
    private static bool Merge(Delegation work, WorkReport report)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(report.Title) && report.Title != work.Title)
            (work.Title, changed) = (report.Title, true);
        if (!string.IsNullOrWhiteSpace(report.Label) && report.Label != work.Label)
            (work.Label, changed) = (report.Label, true);
        if (!string.IsNullOrWhiteSpace(report.ToolCallId) && work.ParentToolCallId is null)
            (work.ParentToolCallId, changed) = (report.ToolCallId, true);
        if (report.Background is { } background && background != work.Background)
            (work.Background, changed) = (background, true);
        if (report.CanStop is { } canStop && canStop != work.CanStop)
            (work.CanStop, changed) = (canStop, true);
        if (report.CanReadOutput is { } canReadOutput && canReadOutput != work.CanReadOutput)
            (work.CanReadOutput, changed) = (canReadOutput, true);
        if (!string.IsNullOrWhiteSpace(report.Detail) && report.Detail != work.Detail)
            (work.Detail, changed) = (report.Detail, true);
        return changed;
    }

    /// <summary>
    /// Tells the session's open conversation, and every client's session list and status bar (the <c>sessions</c>
    /// topic), what the work is now.
    /// </summary>
    private async Task BroadcastWorkAsync(string eventType, Delegation work)
    {
        var payload = JsonSerializer.SerializeToElement(ToItem(work), ApplicationJsonContext.Default.RunningWorkItem);
        await eventBroadcaster.BroadcastAsync($"session:{work.ParentSessionId}", eventType, payload, userContext.UserId, CancellationToken.None)
            .ConfigureAwait(false);
        await eventBroadcaster.BroadcastAsync("sessions", eventType, payload, userContext.UserId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static void ValidateRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", paramName);
    }

    private static void ValidateTerminalStatus(string status)
    {
        ValidateRequired(status, nameof(status));
        if (!TerminalStatuses.Contains(status))
            throw new ArgumentException($"Unsupported terminal status '{status}'.", nameof(status));
    }

    private OutboxMessage CreateOutboxMessage(string parentSessionId, string eventType, Delegation delegation, string createdAt)
    {
        return new OutboxMessage
        {
            Topic = $"session:{parentSessionId}",
            Type = eventType,
            Payload = JsonSerializer.Serialize(new DelegationEventDto(
                delegation.Id,
                delegation.ParentSessionId,
                delegation.ParentToolCallId,
                delegation.ChildSessionId,
                delegation.Title,
                delegation.Status,
                delegation.CreatedAt,
                IsInBackground(delegation)),
                ApplicationJsonContext.Default.DelegationEventDto),
            UserId = userContext.UserId,
            CreatedAt = createdAt,
            AvailableAt = createdAt
        };
    }

    private async Task BroadcastAsync(string parentSessionId, string eventType, Delegation delegation)
    {
        var dto = new DelegationEventDto(
            delegation.Id,
            delegation.ParentSessionId,
            delegation.ParentToolCallId,
            delegation.ChildSessionId,
            delegation.Title,
            delegation.Status,
            delegation.CreatedAt,
            IsInBackground(delegation));
        await eventBroadcaster.BroadcastAsync(
            $"session:{parentSessionId}",
            eventType,
            JsonSerializer.SerializeToElement(dto, ApplicationJsonContext.Default.DelegationEventDto),
            userContext.UserId,
            CancellationToken.None);
    }

    private bool? IsInBackground(Delegation delegation)
        => delegation.ChildSessionId is { } childSessionId && activityTracker?.IsChildInBackground(childSessionId) == true ? true : null;

    private static DelegationDto ToDto(Delegation delegation) => new(
        delegation.Id,
        delegation.ParentToolCallId,
        delegation.ChildSessionId,
        delegation.Title,
        delegation.Status,
        delegation.CreatedAt);
}
