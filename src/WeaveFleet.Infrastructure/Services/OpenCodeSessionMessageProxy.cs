using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Proxy for retrieving session messages from either the live opencode harness (if available)
/// or the persisted message store (fallback).
/// </summary>
public sealed class OpenCodeSessionMessageProxy(
    ISessionRepository sessionRepository,
    InstanceTracker instanceTracker,
    SessionActivityTracker activityTracker,
    IDelegationRepository delegationRepository,
    ISessionSnapshotBuilder fallbackSnapshotBuilder,
    IServiceProvider serviceProvider,
    ILogger<OpenCodeSessionMessageProxy> logger) : ISessionMessageProxy
{
    private const string IdleStatus = "idle";
    private const string BusyStatus = "busy";

    private static readonly Action<ILogger, string, Exception?> LogFetchingFromHarness =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "FetchingFromHarness"),
            "Fetching messages for session {SessionId} from live opencode harness.");

    private static readonly Action<ILogger, string, Exception?> LogFallingBackToPersisted =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, "FallingBackToPersisted"),
            "Falling back to persisted messages for session {SessionId}.");

    private static readonly Action<ILogger, string, Exception?> LogHarnessUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "HarnessUnavailable"),
            "Opencode harness unavailable for session {SessionId}, using persisted messages.");

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
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var session = await sessionRepository.GetByIdAsync(fleetSessionId).ConfigureAwait(false);
        if (session is null)
            throw new InvalidOperationException($"Session '{fleetSessionId}' was not found.");

        // Check if this is an opencode session with a live harness
        if (session.HarnessType == "opencode" && !string.IsNullOrWhiteSpace(session.InstanceId))
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
        
        // Mark the snapshot as partial since we couldn't fetch from the live harness
        return fallbackSnapshot with { IsPartial = true };
    }

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(
        string fleetSessionId,
        int? limit = null,
        string? before = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetSessionId);

        var session = await sessionRepository.GetByIdAsync(fleetSessionId).ConfigureAwait(false);
        if (session is null)
            throw new InvalidOperationException($"Session '{fleetSessionId}' was not found.");

        // Check if this is an opencode session with a live harness
        if (session.HarnessType == "opencode" && !string.IsNullOrWhiteSpace(session.InstanceId))
        {
            var harnessSession = instanceTracker.Get(session.InstanceId);
            if (harnessSession is not null)
            {
                try
                {
                    LogFetchingFromHarness(logger, fleetSessionId, null);
                    var query = new MessageQuery(limit, before);
                    return await harnessSession.GetMessagesAsync(query, ct).ConfigureAwait(false);
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
                        var query = new MessageQuery(limit, before);
                        return await harnessSession.GetMessagesAsync(query, ct).ConfigureAwait(false);
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
        var messages = snapshot.Messages.Select(ToHarnessMessage).ToList();
        return new MessagePage(messages, snapshot.HasMore);
    }

    private async Task<SessionSnapshot> BuildSnapshotFromHarnessAsync(
        Session session,
        IHarnessSession harnessSession,
        int pageSize,
        string? cursor,
        CancellationToken ct)
    {
        // Fetch messages from the live harness
        var query = new MessageQuery(pageSize, cursor);
        var messagePage = await harnessSession.GetMessagesAsync(query, ct).ConfigureAwait(false);

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
            }).ToList(),
            ActivityStatus = activityStatus,
            LastEventId = null, // Live harness doesn't use event IDs
            HasMore = messagePage.HasMore,
            Cursor = messagePage.HasMore && messages.Count > 0 ? messages[0].Info.Id : null,
            IsPartial = false, // Live harness data is complete
        };
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
                TextPart textPart => new TextMessageEventPart
                {
                    Id = $"{message.Id}-text-{textIndex++}",
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    Text = textPart.Text,
                },
                ReasoningPart reasoningPart => new ReasoningMessageEventPart
                {
                    Id = $"{message.Id}-reasoning-{reasoningIndex++}",
                    SessionId = fleetSessionId,
                    MessageId = message.Id,
                    Text = reasoningPart.Text,
                    Summary = reasoningPart.Summary,
                },
                ToolUsePart toolPart => new ToolMessageEventPart
                {
                    Id = $"{message.Id}-tool-{toolIndex++}",
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
            ToolUseState.Running => new ToolRunningState { Input = input },
            ToolUseState.Completed => new ToolCompletedState { Input = input, Output = null },
            ToolUseState.Error => new ToolErrorState { Input = input, Output = null },
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
                TextMessageEventPart textPart => new TextPart(textPart.Text),
                ReasoningMessageEventPart reasoningPart => new ReasoningPart(reasoningPart.Text, reasoningPart.Summary),
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
                    }),
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

    private static string NormalizeActivityStatus(string? activityStatus)
        => string.Equals(activityStatus, BusyStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(activityStatus, "working", StringComparison.OrdinalIgnoreCase)
                ? BusyStatus
                : IdleStatus;
}
