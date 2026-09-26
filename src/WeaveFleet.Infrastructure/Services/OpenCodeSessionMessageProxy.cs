using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Proxy for retrieving session messages from either the live harness (if available and it keeps the
/// history, see <see cref="HarnessCapabilities.HistoryLivesInHarness"/>) or the persisted message store (fallback).
/// </summary>
public sealed class OpenCodeSessionMessageProxy(
    ISessionRepository sessionRepository,
    InstanceTracker instanceTracker,
    SessionActivityTracker activityTracker,
    IDelegationRepository delegationRepository,
    ISessionSnapshotBuilder fallbackSnapshotBuilder,
    IServiceProvider serviceProvider,
    IHarnessRegistry harnessRegistry,
    ILogger<OpenCodeSessionMessageProxy> logger,
    IMessageRepository? messageRepository = null) : ISessionMessageProxy
{
    private const string IdleStatus = "idle";
    private const string BusyStatus = "busy";
    private const string RetryStatus = "retry";

    private static readonly Action<ILogger, string, Exception?> LogFetchingFromHarness =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "FetchingFromHarness"),
            "Fetching messages for session {SessionId} from the live harness.");

    private static readonly Action<ILogger, string, Exception?> LogFallingBackToPersisted =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, "FallingBackToPersisted"),
            "Falling back to persisted messages for session {SessionId}.");

    private static readonly Action<ILogger, string, Exception?> LogHarnessUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "HarnessUnavailable"),
            "Harness unavailable for session {SessionId}, using persisted messages.");

    private static readonly Action<ILogger, string, Exception?> LogAttemptingResume =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4, "AttemptingResume"),
            "Attempting to resume session {SessionId} before falling back to persisted messages.");

    private static readonly Action<ILogger, string, Exception?> LogResumeFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5, "ResumeFailed"),
            "Failed to resume session {SessionId}, falling back to persisted messages.");

    /// <inheritdoc />
    public async Task<SessionSnapshot> GetSnapshotAsync(
        string fleetSessionId,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken ct = default)
    {
        var snapshot = await ReadSnapshotAsync(fleetSessionId, pageSize, cursor, ct).ConfigureAwait(false);
        if (InTurn(fleetSessionId))
            return snapshot;

        var stillWorking = CutOffToolCalls.StillWorking(
            snapshot.Delegations.Select(d => (d.ParentToolCallId, d.ChildSessionId)),
            ChildStillWorking);
        return snapshot with { Messages = CutOffToolCalls.Settle(snapshot.Messages, stillWorking) };
    }

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(
        string fleetSessionId,
        int? limit = null,
        string? before = null,
        CancellationToken ct = default)
    {
        var page = await ReadMessagesAsync(fleetSessionId, limit, before, ct).ConfigureAwait(false);
        if (InTurn(fleetSessionId) || !page.Messages.Any(HasUnfinishedCall))
            return page;

        var delegations = await delegationRepository.GetByParentSessionIdAsync(fleetSessionId).ConfigureAwait(false);
        var stillWorking = CutOffToolCalls.StillWorking(
            delegations.Select(d => (d.ParentToolCallId, d.ChildSessionId)),
            ChildStillWorking);
        return page with { Messages = CutOffToolCalls.Settle(page.Messages, stillWorking) };
    }

    /// <summary>Whether the session is in a turn: working, retrying, or stopped on a question. Only then can a call run.</summary>
    private bool InTurn(string sessionId)
        => SessionActivityTracker.IsInTurn(activityTracker.GetEffectiveActivityStatus(sessionId));

    private bool ChildStillWorking(string childSessionId)
        => activityTracker.IsChildInBackground(childSessionId) || InTurn(childSessionId);

    private static bool HasUnfinishedCall(HarnessMessage message)
        => message.Parts.Any(part => part is ToolUsePart { State: ToolUseState.Pending or ToolUseState.Running });

    private async Task<SessionSnapshot> ReadSnapshotAsync(
        string fleetSessionId,
        int pageSize,
        string? cursor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var session = await sessionRepository.GetByIdAsync(fleetSessionId).ConfigureAwait(false);
        if (session is null)
            throw new InvalidOperationException($"Session '{fleetSessionId}' was not found.");

        // Read the harness's own history when it keeps one and the session is running
        if (HistoryLivesInHarness(session) && !string.IsNullOrWhiteSpace(session.InstanceId))
        {
            var harnessSession = instanceTracker.Get(session.InstanceId);
            if (harnessSession is not null)
            {
                try
                {
                    LogFetchingFromHarness(logger, fleetSessionId, null);
                    return await BuildSnapshotFromHarnessAsync(session, harnessSession, pageSize, cursor, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    LogHarnessUnavailable(logger, fleetSessionId, ex);
                    // Fall through to persisted fallback
                }
            }
            else if (!string.IsNullOrWhiteSpace(session.HarnessResumeToken))
            {
                // Harness is missing but we have a resume token - attempt lazy resume
                // Resolve ISessionActivator lazily to avoid DI cycle with SessionOrchestrator
                LogAttemptingResume(logger, fleetSessionId, null);
                try
                {
                    var sessionActivator = serviceProvider.GetRequiredService<ISessionActivator>();
                    await sessionActivator.ActivateSessionAsync(fleetSessionId, ct).ConfigureAwait(false);
                    
                    // Refetch session to get the updated instance ID after activation
                    var resumedSession = await sessionRepository.GetByIdAsync(fleetSessionId).ConfigureAwait(false);
                    if (resumedSession is not null && !string.IsNullOrWhiteSpace(resumedSession.InstanceId))
                    {
                        harnessSession = instanceTracker.Get(resumedSession.InstanceId);
                        if (harnessSession is not null)
                        {
                            LogFetchingFromHarness(logger, fleetSessionId, null);
                            return await BuildSnapshotFromHarnessAsync(resumedSession, harnessSession, pageSize, cursor, ct)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogResumeFailed(logger, fleetSessionId, ex);
                    // Fall through to persisted fallback
                }
            }
        }

        // Fall back to persisted messages
        LogFallingBackToPersisted(logger, fleetSessionId, null);
        var fallbackSnapshot = await fallbackSnapshotBuilder.BuildAsync(fleetSessionId, pageSize, cursor)
            .ConfigureAwait(false);
        fallbackSnapshot = fallbackSnapshot with
        {
            Messages = await WithCommandsAsync(fleetSessionId, fallbackSnapshot.Messages).ConfigureAwait(false),
        };

        // Mark the snapshot partial when the history lives in the harness and we couldn't read it. Other
        // harnesses (Claude Code) keep their history in Fleet's database, so theirs is complete.
        return HistoryLivesInHarness(session)
            ? fallbackSnapshot with { IsPartial = true }
            : fallbackSnapshot;
    }

    private async Task<MessagePage> ReadMessagesAsync(
        string fleetSessionId,
        int? limit,
        string? before,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);

        var session = await sessionRepository.GetByIdAsync(fleetSessionId).ConfigureAwait(false);
        if (session is null)
            throw new InvalidOperationException($"Session '{fleetSessionId}' was not found.");

        // Read the harness's own history when it keeps one and the session is running
        if (HistoryLivesInHarness(session) && !string.IsNullOrWhiteSpace(session.InstanceId))
        {
            var harnessSession = instanceTracker.Get(session.InstanceId);
            if (harnessSession is not null)
            {
                try
                {
                    LogFetchingFromHarness(logger, fleetSessionId, null);
                    return await GetLiveMessagesAsync(fleetSessionId, harnessSession, limit, before, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    LogHarnessUnavailable(logger, fleetSessionId, ex);
                    // Fall through to persisted fallback
                }
            }
            else if (!string.IsNullOrWhiteSpace(session.HarnessResumeToken))
            {
                // Harness is missing but we have a resume token - attempt lazy resume
                // Resolve ISessionActivator lazily to avoid DI cycle with SessionOrchestrator
                LogAttemptingResume(logger, fleetSessionId, null);
                try
                {
                    var sessionActivator = serviceProvider.GetRequiredService<ISessionActivator>();
                    await sessionActivator.ActivateSessionAsync(fleetSessionId, ct).ConfigureAwait(false);
                    
                    // Check if resume succeeded by looking for the harness again
                    harnessSession = instanceTracker.Get(session.InstanceId);
                    if (harnessSession is not null)
                    {
                        LogFetchingFromHarness(logger, fleetSessionId, null);
                        return await GetLiveMessagesAsync(fleetSessionId, harnessSession, limit, before, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    LogResumeFailed(logger, fleetSessionId, ex);
                    // Fall through to persisted fallback
                }
            }
        }

        // Fall back to persisted messages via snapshot builder
        LogFallingBackToPersisted(logger, fleetSessionId, null);
        var snapshot = await fallbackSnapshotBuilder.BuildAsync(fleetSessionId, limit ?? 100, before)
            .ConfigureAwait(false);

        // Convert MessageLifecyclePayload to HarnessMessage
        var messages = (await WithCommandsAsync(fleetSessionId, snapshot.Messages).ConfigureAwait(false))
            .Select(ToHarnessMessage)
            .ToList();
        return new MessagePage(messages, snapshot.HasMore, snapshot.Cursor);
    }

    private bool HistoryLivesInHarness(Session session)
        => harnessRegistry.GetByType(session.HarnessType)?.Capabilities.HistoryLivesInHarness == true;

    private async Task<SessionSnapshot> BuildSnapshotFromHarnessAsync(
        Session session,
        IHarnessSession harnessSession,
        int pageSize,
        string? cursor,
        CancellationToken ct)
    {
        // Fetch messages from the live harness
        var messagePage = await GetLiveMessagesAsync(session.Id, harnessSession, pageSize, cursor, ct)
            .ConfigureAwait(false);

        // Convert HarnessMessage to MessageLifecyclePayload
        var messages = messagePage.Messages.Select(m => ToMessageLifecyclePayload(m, session.Id)).ToList();

        // Fetch delegations from the database
        var delegations = await delegationRepository.GetByParentSessionIdAsync(session.Id)
            .ConfigureAwait(false);

        // Get activity status
        var activityStatus = NormalizeActivityStatus(activityTracker.GetEffectiveActivityStatus(session.Id));

        return new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = session.Id,
                Title = session.Title,
                Status = session.Status,
            },
            Messages = messages,
            Delegations = delegations.Select(d => new SessionSnapshotDelegation
            {
                DelegationId = d.Id,
                ParentToolCallId = d.ParentToolCallId,
                ChildSessionId = d.ChildSessionId,
                Title = d.Title,
                Status = d.Status,
                CreatedAt = d.CreatedAt,
                ChildActivityStatus = d.ChildSessionId is { } childSessionId
                    ? activityTracker.GetEffectiveActivityStatus(childSessionId)
                    : null,
                Background = d.ChildSessionId is { } backgroundChildId && activityTracker.IsChildInBackground(backgroundChildId)
                    ? true
                    : null,
            }).ToList(),
            ActivityStatus = activityStatus,
            LastEventId = null, // Live harness doesn't use event IDs
            HasMore = messagePage.HasMore,
            // The harness's own cursor: OpenCode rejects a message id as "before".
            Cursor = messagePage.HasMore ? messagePage.Cursor : null,
            IsPartial = false, // Live harness data is complete
        };
    }

    private async Task<MessagePage> GetLiveMessagesAsync(
        string fleetSessionId,
        IHarnessSession harnessSession,
        int? limit,
        string? before,
        CancellationToken ct)
    {
        var page = await harnessSession.GetMessagesAsync(new MessageQuery(limit, before), ct).ConfigureAwait(false);
        page = await WithCommandsAsync(fleetSessionId, page).ConfigureAwait(false);
        return before is null
            ? await AddPromptsTheHarnessHasNotStoredAsync(fleetSessionId, page).ConfigureAwait(false)
            : page;
    }

    /// <summary>
    /// Marks the user messages that came from a slash command with the command, so they show as <c>/name arguments</c>
    /// rather than the prompt the harness made of it.
    /// </summary>
    private async Task<MessagePage> WithCommandsAsync(string fleetSessionId, MessagePage page)
    {
        var commands = await GetCommandsAsync(fleetSessionId, page.Messages.Select(m => m.Role)).ConfigureAwait(false);
        return commands.Count == 0
            ? page
            : page with
            {
                Messages = [.. page.Messages.Select(m => m.Role == "user" && commands.TryGetValue(m.Id, out var command)
                    ? m with { Command = command }
                    : m)],
            };
    }

    /// <inheritdoc cref="WithCommandsAsync(string, MessagePage)"/>
    private async Task<IReadOnlyList<MessageLifecyclePayload>> WithCommandsAsync(
        string fleetSessionId,
        IReadOnlyList<MessageLifecyclePayload> messages)
    {
        var commands = await GetCommandsAsync(fleetSessionId, messages.Select(m => m.Info.Role)).ConfigureAwait(false);
        return commands.Count == 0
            ? messages
            : [.. messages.Select(m => m.Info.Role == "user" && commands.TryGetValue(m.Info.Id, out var command)
                ? m with { Info = m.Info with { Command = command } }
                : m)];
    }

    /// <summary>The session's commands by message id; none are read for a page without a user message.</summary>
    private async Task<IReadOnlyDictionary<string, SlashCommand>> GetCommandsAsync(string fleetSessionId, IEnumerable<string> roles)
        => messageRepository is not null && roles.Contains("user")
            ? await messageRepository.GetCommandsAsync(fleetSessionId).ConfigureAwait(false)
            : new Dictionary<string, SlashCommand>();

    /// <summary>
    /// Adds prompts Fleet saved when it sent them (a new session's first message) that the harness
    /// hasn't stored yet. The page asks for the snapshot right after create, which can beat the
    /// harness to it. The harness stores a prompt under the id Fleet saved it with, so a prompt drops
    /// out as soon as the harness has it; anything older than the harness's newest message is not pending.
    /// </summary>
    private async Task<MessagePage> AddPromptsTheHarnessHasNotStoredAsync(string fleetSessionId, MessagePage page)
    {
        if (messageRepository is null)
            return page;

        var saved = await messageRepository.GetBySessionAsync(fleetSessionId, limit: 5, beforeMessageId: null)
            .ConfigureAwait(false);
        if (saved.Count == 0)
            return page;

        var harnessIds = page.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var newestInHarness = page.Messages.Count > 0 ? page.Messages.Max(m => m.Timestamp) : DateTimeOffset.MinValue;
        var pending = MessagePersistenceService.ToHarnessMessages(saved)
            .Where(m => m.Role == "user" && !harnessIds.Contains(m.Id) && m.Timestamp > newestInHarness)
            .OrderBy(m => m.Timestamp)
            .ToList();

        return pending.Count == 0 ? page : page with { Messages = [.. page.Messages, .. pending] };
    }

    private static MessageLifecyclePayload ToMessageLifecyclePayload(HarnessMessage message, string fleetSessionId)
    {
        var parts = new List<MessageEventPart>();
        int textIndex = 0;
        int toolIndex = 0;
        int fileIndex = 0;
        int reasoningIndex = 0;

        foreach (var part in message.Parts)
        {
            MessageEventPart? eventPart = part switch
            {
                // The harness's part ids come first: live updates name parts by them, so a part still
                // streaming when the session is reopened updates in place instead of appearing twice.
                TextPart textPart => new TextMessageEventPart
                {
                    Id = textPart.PartId ?? $"{message.Id}-text-{textIndex++}",
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    Text = textPart.Text,
                },
                ReasoningPart reasoningPart => new ReasoningMessageEventPart
                {
                    Id = reasoningPart.PartId ?? $"{message.Id}-reasoning-{reasoningIndex++}",
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    Text = reasoningPart.Text,
                    Summary = reasoningPart.Summary,
                },
                ToolUsePart toolPart => new ToolMessageEventPart
                {
                    Id = toolPart.PartId ?? $"{message.Id}-tool-{toolIndex++}",
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    ToolName = toolPart.ToolName,
                    CallId = toolPart.ToolCallId,
                    State = MapToolState(toolPart),
                },
                FilePart filePart => new FileMessageEventPart
                {
                    Id = string.IsNullOrWhiteSpace(filePart.PartId) ? $"{message.Id}-file-{fileIndex++}" : filePart.PartId,
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    Mime = filePart.Mime,
                    Url = filePart.Url,
                    Filename = filePart.Filename,
                },
                StepFinishPart stepFinishPart => new StepFinishedMessageEventPart
                {
                    Id = $"{message.Id}-step-finish-{stepFinishPart.Index}",
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    Index = stepFinishPart.Index,
                    Reason = stepFinishPart.Reason,
                    Cost = stepFinishPart.Cost,
                    Tokens = new MessageTokenUsage
                    {
                        Input = stepFinishPart.TokensInput,
                        Output = stepFinishPart.TokensOutput,
                        Reasoning = stepFinishPart.TokensReasoning,
                    },
                    CompletedAt = stepFinishPart.CompletedAt,
                },
                _ => null,
            };

            if (eventPart is not null)
                parts.Add(eventPart);
        }

        return new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = message.Id,
                Role = message.Role,
                SessionId = fleetSessionId,
                Agent = message.Agent,
                ModelId = message.ModelId,
                Time = new MessageEventTime
                {
                    Created = message.Timestamp.ToUnixTimeMilliseconds(),
                },
                // Carried into the snapshot so a failed turn still says why it failed after a reload.
                Error = message.Error,
                Finish = message.Finish,
                // So a prompt sent into a running turn still says so after a reload.
                Steered = message.Steered ? true : null,
                Command = message.Command,
            },
            Parts = parts,
        };
    }

    private static ToolInvocationState MapToolState(ToolUsePart toolPart)
    {
        var input = toolPart.Arguments.ValueKind == JsonValueKind.Undefined
            ? (JsonElement?)null
            : toolPart.Arguments.Clone();

        return toolPart.State switch
        {
            ToolUseState.Pending => new ToolPendingState { Input = input },
            // A call moved into the background has returned: it carries what it answered with while its work runs.
            ToolUseState.Running => new ToolRunningState
            {
                Input = input,
                Output = toolPart.Output?.Clone(),
                Metadata = toolPart.Metadata?.Clone(),
                Background = toolPart.Background,
            },
            ToolUseState.Completed => new ToolCompletedState
            {
                Input = input,
                Output = toolPart.Output?.Clone(),
                Title = toolPart.Title,
                Metadata = toolPart.Metadata?.Clone(),
            },
            ToolUseState.Error => new ToolErrorState
            {
                Input = input,
                Output = toolPart.Output?.Clone(),
                Error = toolPart.Error,
                Metadata = toolPart.Metadata?.Clone(),
            },
            _ => new ToolPendingState { Input = input },
        };
    }

    private static HarnessMessage ToHarnessMessage(MessageLifecyclePayload payload)
    {
        var parts = new List<MessagePart>();

        foreach (var eventPart in payload.Parts)
        {
            MessagePart? part = eventPart switch
            {
                TextMessageEventPart textPart => new TextPart(textPart.Text) { PartId = textPart.Id },
                ReasoningMessageEventPart reasoningPart => new ReasoningPart(reasoningPart.Text, reasoningPart.Summary) { PartId = reasoningPart.Id },
                ToolMessageEventPart toolPart => new ToolUsePart(
                    toolPart.CallId,
                    toolPart.ToolName,
                    ExtractToolInput(toolPart.State),
                    toolPart.State switch
                    {
                        ToolPendingState => ToolUseState.Pending,
                        ToolRunningState => ToolUseState.Running,
                        ToolCompletedState => ToolUseState.Completed,
                        ToolErrorState => ToolUseState.Error,
                        _ => ToolUseState.Pending,
                    })
                {
                    PartId = toolPart.Id,
                    Output = toolPart.State switch
                    {
                        ToolCompletedState completed => completed.Output,
                        ToolErrorState error => error.Output,
                        ToolRunningState running => running.Output,
                        _ => null,
                    },
                    Error = (toolPart.State as ToolErrorState)?.Error,
                    Title = (toolPart.State as ToolCompletedState)?.Title,
                    Metadata = toolPart.State switch
                    {
                        ToolCompletedState completed => completed.Metadata,
                        ToolErrorState error => error.Metadata,
                        ToolRunningState running => running.Metadata,
                        _ => null,
                    },
                    Background = toolPart.State is ToolRunningState { Background: true },
                },
                FileMessageEventPart filePart => new FilePart(eventPart.Id, filePart.Mime, filePart.Url, filePart.Filename),
                StepFinishedMessageEventPart stepPart => new StepFinishPart(
                    stepPart.Index,
                    stepPart.Reason,
                    stepPart.Cost,
                    stepPart.Tokens?.Input ?? 0,
                    stepPart.Tokens?.Output ?? 0,
                    stepPart.Tokens?.Reasoning ?? 0,
                    stepPart.CompletedAt),
                _ => null,
            };

            if (part is not null)
                parts.Add(part);
        }

        return new HarnessMessage
        {
            Id = payload.Info.Id,
            Role = payload.Info.Role,
            Parts = parts,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(payload.Info.Time.Created),
            Agent = payload.Info.Agent,
            ModelId = payload.Info.ModelId,
            Steered = payload.Info.Steered == true,
            Command = payload.Info.Command,
        };
    }

    private static JsonElement ExtractToolInput(ToolInvocationState state)
    {
        return state switch
        {
            ToolPendingState pending => pending.Input ?? default(JsonElement),
            ToolRunningState running => running.Input ?? default(JsonElement),
            ToolCompletedState completed => completed.Input ?? default(JsonElement),
            ToolErrorState error => error.Input ?? default(JsonElement),
            _ => default(JsonElement),
        };
    }

    // A retrying session is still working; reporting it as idle made it look done on reopen.
    private static string NormalizeActivityStatus(string? activityStatus)
        => string.Equals(activityStatus, BusyStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(activityStatus, "working", StringComparison.OrdinalIgnoreCase)
                ? BusyStatus
                : string.Equals(activityStatus, RetryStatus, StringComparison.OrdinalIgnoreCase)
                    ? RetryStatus
                    // Stopped on a question (its own or a subagent's): opening the session mustn't make it read idle.
                    : string.Equals(activityStatus, ActivityStatuses.WaitingInput, StringComparison.OrdinalIgnoreCase)
                        ? ActivityStatuses.WaitingInput
                        : IdleStatus;
}
