using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Prompting;

/// <summary>
/// What the user sends a session: prompts (queued or steered into a running turn), slash commands, a source's context,
/// and Stop. Every prompt wakes the session if it must, tells the harness the permission level, shows the message at
/// once under the id the harness is given, and carries the notes the harness passes on unseen (side-conversation
/// boundary, memory changes, lost work). The agent or model a prompt names becomes the session's.
/// </summary>
public sealed partial class SessionPrompting(
    ISessionRepository sessionRepository,
    IHarnessRegistry harnessRegistry,
    SessionActivation activation,
    SessionActivityTracker sessionActivityTracker,
    DelegationService delegationService,
    SessionSourceResolutionService sessionSourceResolutionService,
    ISessionSourceUsageRepository sessionSourceUsageRepository,
    IEventBroadcaster eventBroadcaster,
    IUserContext userContext,
    ILogger<SessionPrompting> logger,
    IMessageRepository? messageRepository = null,
    SessionRecapService? sessionRecaps = null,
    AgentMemoryService? agentMemory = null)
{
    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options = null,
        CancellationToken ct = default)
        => await PromptSessionAsync(id, text, options, userMessageId: null, correlationId: null, ct).ConfigureAwait(false);

    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        CancellationToken ct)
        => await PromptSessionAsync(id, text, options, userMessageId, correlationId: null, ct).ConfigureAwait(false);

    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
    {
        var result = await PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, saveUserMessage: false, rememberChoices: true, ct).ConfigureAwait(false);
        return result.IsSuccess ? Unit.Value : result.Error;
    }

    public async Task<Result<PromptSessionResult>> PromptSessionWithReceiptAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
        => await PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, saveUserMessage: false, rememberChoices: true, ct).ConfigureAwait(false);

    /// <summary>
    /// Prompts a session with an agent or model for this prompt only; later prompts that name none still get the
    /// session's own. Automations prompt existing sessions this way, so a run on a cheaper model doesn't change
    /// the model the session's owner picked.
    /// </summary>
    public async Task<Result<Unit>> PromptSessionOnceAsync(
        string id,
        string text,
        PromptOptions? options,
        CancellationToken ct = default)
    {
        var result = await PromptSessionCoreAsync(id, text, options, userMessageId: null, correlationId: null, saveUserMessage: false, rememberChoices: false, ct).ConfigureAwait(false);
        return result.IsSuccess ? Unit.Value : result.Error;
    }

    /// <summary>
    /// Every prompt goes through here: the session's own creation (its first message) and side questions too.
    /// <paramref name="saveUserMessage"/> keeps a copy of the message for the snapshot until the harness has it;
    /// <paramref name="rememberChoices"/> makes an agent or model the prompt names the session's from now on.
    /// </summary>
    internal async Task<Result<PromptSessionResult>> PromptSessionCoreAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        bool saveUserMessage,
        bool rememberChoices,
        CancellationToken ct)
    {
        using var promptActivity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.prompt_session",
            ActivityKind.Internal);
        promptActivity?.SetTag(FleetInstrumentation.SessionIdTag, id);

        // Store trace context so the async relay pump can link response events back to this prompt.
        if (promptActivity is not null)
            sessionActivityTracker.SetPromptTraceContext(id, promptActivity.Context);

        using var _ = logger.BeginSessionScope(id);
        var sessionResult = await sessionRepository.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        // A harness that can't pass images on would drop them without a word.
        if (options?.Attachments is { Count: > 0 }
            && harnessRegistry.GetByType(sessionResult.Value.HarnessType)?.Capabilities.SupportsImageAttachments != true)
        {
            return FleetError.ValidationError("Prompt.Attachments", "This session's harness can't take images. Send the prompt without them.");
        }

        var delivery = SteeringDelivery(options?.Delivery, sessionResult.Value, out var steeringError);
        if (steeringError is not null)
            return steeringError;
        if (options is not null && delivery != options.Delivery)
            options = options with { Delivery = delivery };

        // What the caller named is remembered below; what it left out comes from the session.
        var requestedOptions = rememberChoices ? options : null;
        options = WithSessionChoices(options, sessionResult.Value);

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await activation.ApplyPermissionsAsync(sessionResult.Value, instanceResult.Value, ct).ConfigureAwait(false);

        try
        {
            // Generate ascending message ID for the user prompt.
            var generatedMessageId = AscendingMessageId.New();
            
            // Broadcast user message for optimistic UI update.
            // The harness echo is suppressed in HarnessEventPersistenceService to avoid duplicates.
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                ? Guid.NewGuid().ToString()
                : correlationId;
            var userMsg = MessagePersistenceService.CreateUserPromptMessage(
                text,
                DateTimeOffset.UtcNow,
                options?.Agent,
                generatedMessageId,
                options?.Attachments) with { Steered = delivery == PromptDelivery.Steer };

            await BroadcastUserMessageAsync(id, userMsg, effectiveCorrelationId, ct).ConfigureAwait(false);

            // Ensure the event subscription is established before sending the prompt.
            // This prevents early events from being lost during activation/resume.
            await activation.EnsureEventSubscriptionReadyAsync(instanceResult.Value, id, ct).ConfigureAwait(false);

            // With memory on, write what the session's folder reads before the model sees the prompt: OpenCode reads that
            // file at a session's first model request. A harness that doesn't read the file (Claude Code) takes the notes
            // with the prompt instead; with Fleet's tools it saves notes as OpenCode does, and without them only reads them.
            var memoryNotes = agentMemory is null
                ? null
                : await agentMemory.PrepareSessionAsync(
                    sessionResult.Value.UserId,
                    sessionResult.Value.Directory,
                    canSave: harnessRegistry.GetByType(sessionResult.Value.HarnessType)?.Capabilities.SupportsFleetTools == true,
                    ct).ConfigureAwait(false);

            // A session's instructions keep the notes it started with, so a change since then goes with this prompt, in
            // the notes the harness gives the model unseen. Only the harnesses that pass those notes on are told.
            var modelNotes = SideConversations.ModelNotesFor(sessionResult.Value);
            if (agentMemory is not null
                && harnessRegistry.GetByType(sessionResult.Value.HarnessType)?.Capabilities.SupportsSideConversations == true
                && await agentMemory.ChangesForAsync(sessionResult.Value, ct).ConfigureAwait(false) is { } memoryChanges)
                modelNotes = [.. modelNotes ?? [], memoryChanges];

            // Work the agent left running that was lost (Fleet or its harness process restarted) won't report back: the
            // first prompt after says which, once, to a harness that passes Fleet's notes on.
            var lostWork = harnessRegistry.GetByType(sessionResult.Value.HarnessType)?.Capabilities.TakesModelNotes == true
                ? await delegationService.GetUnreportedLostWorkAsync(id).ConfigureAwait(false)
                : [];
            if (LostWorkNote.For(lostWork) is { } lostWorkNote)
                modelNotes = [.. modelNotes ?? [], lostWorkNote];

            // Pass the generated message ID through to the harness, with any notes the session's prompts carry.
            var promptOptionsWithMessageId = options is null
                ? new PromptOptions { MessageId = generatedMessageId, ModelNotes = modelNotes, MemoryNotes = memoryNotes }
                : options with { MessageId = generatedMessageId, ModelNotes = modelNotes, MemoryNotes = memoryNotes };

            await instanceResult.Value.SendPromptAsync(text, promptOptionsWithMessageId, ct);
            if (lostWork.Count > 0)
                await delegationService.MarkLostWorkReportedAsync(lostWork).ConfigureAwait(false);

            // Your reply is what a recap waits for: it clears the current one and counts toward the next. A side
            // conversation gets none: it's only ever seen beside its session.
            if (sessionRecaps is not null && sessionResult.Value.SideOfSessionId is null)
                await sessionRecaps.OnPromptSentAsync(id, sessionResult.Value.UserId, ct).ConfigureAwait(false);

            // Saved under the id the harness was given, so the snapshot can tell when the harness has its own copy.
            if (saveUserMessage && messageRepository is not null)
                await messageRepository.UpsertAsync(MessagePersistenceService.ToPersistedMessage(id, userMsg)).ConfigureAwait(false);

            // Remember an agent or model the prompt named, so later prompts that name none (after a refresh,
            // from an automation, or with "Default" picked) keep it rather than drop to the harness's default.
            // A model is only remembered as a pair; the API resolves the pair before it gets here.
            if (HasModel(requestedOptions?.ProviderId, requestedOptions?.ModelId)
                && (requestedOptions!.ProviderId != sessionResult.Value.SelectedProviderId
                    || requestedOptions.ModelId != sessionResult.Value.SelectedModelId))
            {
                await sessionRepository.UpdateSelectedModelAsync(id, requestedOptions.ProviderId!, requestedOptions.ModelId!);
            }

            if (requestedOptions?.Agent is { Length: > 0 } agent
                && !string.Equals(agent, sessionResult.Value.SelectedAgent, StringComparison.Ordinal))
            {
                await sessionRepository.UpdateSelectedAgentAsync(id, agent);
            }

            return new PromptSessionResult(EventId: null, effectiveCorrelationId, generatedMessageId);
        }
        catch (InvalidOperationException ex)
        {
            LogPromptFailed(ex, id);
            return new FleetError("Session.PromptFailed", ex.Message);
        }
        catch (Exception ex)
        {
            LogPromptUnexpectedFailure(ex, id);
            return FleetError.Unexpected;
        }
    }

    public async Task<Result<ContextEnvelope>> PreviewAddSourceToSessionAsync(
        string sessionId,
        SessionSourceSelection source,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "Session.RetentionStatus",
                "Archived sessions are read-only.");
        }

        var resolutionResult = await sessionSourceResolutionService.ResolveForSessionActionAsync(
            sessionId,
            source,
            SessionSourceActions.AddToSession,
            ct);
        if (resolutionResult.IsFailure)
            return resolutionResult.Error;

        var envelope = resolutionResult.Value.Input.ContextEnvelope;
        if (envelope is null)
        {
            return FleetError.ValidationError(
                "SessionSource.ContextEnvelope",
                "The selected session source did not resolve any previewable context.");
        }

        return envelope;
    }

    public async Task<Result<Unit>> AddSourceToSessionAsync(
        string sessionId,
        SessionSourceSelection source,
        bool confirm,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        if (!confirm)
        {
            return FleetError.ValidationError(
                "SessionSource.Confirm",
                "Source context must be explicitly confirmed before it can be added to a session.");
        }

        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "Session.RetentionStatus",
                "Archived sessions are read-only.");
        }

        var resolutionResult = await sessionSourceResolutionService.ResolveForSessionActionAsync(
            sessionId,
            source,
            SessionSourceActions.AddToSession,
            ct);
        if (resolutionResult.IsFailure)
            return resolutionResult.Error;

        var envelope = resolutionResult.Value.Input.ContextEnvelope;
        if (envelope is null)
        {
            return FleetError.ValidationError(
                "SessionSource.ContextEnvelope",
                "The selected session source did not resolve any context.");
        }

        var prompt = $"[Source: {envelope.OriginLabel}]\n\n{envelope.Content}";
        var promptResult = await PromptSessionAsync(sessionId, prompt, null, ct);
        if (promptResult.IsFailure)
            return promptResult.Error;

        await sessionSourceUsageRepository.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionId,
            WorkspaceId = session.WorkspaceId,
            ProviderId = resolutionResult.Value.Input.Provenance.ProviderId,
            SourceType = resolutionResult.Value.Input.Provenance.SourceType,
            ActionId = resolutionResult.Value.Input.Provenance.ActionId,
            ResourceId = resolutionResult.Value.Input.Provenance.ResourceId,
            ResourceUrl = resolutionResult.Value.Input.Provenance.ResourceUrl,
            Title = resolutionResult.Value.Input.Provenance.Title,
            Summary = resolutionResult.Value.Input.Provenance.Summary,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        return Unit.Value;
    }

    public async Task<Result<Unit>> AbortSessionAsync(string id, CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var sessionResult = await sessionRepository.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await instanceResult.Value.AbortAsync(ct);
        return Unit.Value;
    }

    public async Task<Result<Unit>> CommandSessionAsync(
        string id,
        CommandOptions options,
        CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var sessionResult = await sessionRepository.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        var instanceResult = await activation.GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await activation.ApplyPermissionsAsync(sessionResult.Value, instanceResult.Value, ct).ConfigureAwait(false);

        // Shown straight away as "/name arguments", under an id that sorts where it was sent. A harness that takes the
        // id stores the command's message under it; one that doesn't says which id it chose.
        options = options with { MessageId = AscendingMessageId.New() };
        var userMsg = MessagePersistenceService.CreateUserCommandMessage(options, DateTimeOffset.UtcNow);
        await BroadcastUserMessageAsync(id, userMsg, ct).ConfigureAwait(false);

        var harnessMessageId = await instanceResult.Value.SendCommandAsync(options, ct);

        // The harness's message holds what it made of the command (OpenCode's whole template). Remembering the command
        // under that message's id lets the conversation show "/name arguments" there too, after a reload.
        if (harnessMessageId is not null && messageRepository is not null && userMsg.Command is { } command)
            await messageRepository.SaveCommandAsync(id, harnessMessageId, command).ConfigureAwait(false);

        return Unit.Value;
    }

    internal async Task BroadcastUserMessageAsync(
        string sessionId,
        HarnessMessage message,
        CancellationToken ct)
    {
        if (message.Role is not "user")
            return;

        var parts = new List<JsonElement>(message.Parts.Count);
        for (var index = 0; index < message.Parts.Count; index++)
        {
            var partPayload = MessagePersistenceService.BuildCommittedMessagePartPayload(
                message.Id,
                sessionId,
                message.Parts[index],
                index);
            if (partPayload.HasValue)
                parts.Add(partPayload.Value);
        }

        var payload = JsonSerializer.SerializeToElement(new CommittedMessage(
            new CommittedMessageInfo(
                message.Id,
                message.Role,
                sessionId,
                message.Agent,
                message.ModelId,
                new CommittedMessageTime(message.Timestamp.ToUnixTimeMilliseconds()),
                message.Steered ? true : null,
                message.Command),
            parts),
            ApplicationJsonContext.Default.CommittedMessage);

        await eventBroadcaster.BroadcastAsync(
            $"session:{sessionId}",
            EventTypes.MessageUpdated,
            payload,
            userContext.UserId,
            ct).ConfigureAwait(false);
    }

    private async Task BroadcastUserMessageAsync(
        string sessionId,
        HarnessMessage message,
        string correlationId,
        CancellationToken ct)
    {
        if (message.Role is not "user")
            return;

        var parts = new List<JsonElement>(message.Parts.Count);
        for (var index = 0; index < message.Parts.Count; index++)
        {
            var partPayload = MessagePersistenceService.BuildCommittedMessagePartPayload(
                message.Id,
                sessionId,
                message.Parts[index],
                index);
            if (partPayload.HasValue)
                parts.Add(partPayload.Value);
        }

        var payload = JsonSerializer.SerializeToElement(new CommittedUserPromptMessage(
            new CommittedMessageInfo(
                message.Id,
                message.Role,
                sessionId,
                message.Agent,
                message.ModelId,
                new CommittedMessageTime(message.Timestamp.ToUnixTimeMilliseconds()),
                message.Steered ? true : null,
                message.Command),
            parts,
            correlationId),
            ApplicationJsonContext.Default.CommittedUserPromptMessage);

        await eventBroadcaster.BroadcastAsync(
            $"session:{sessionId}",
            EventTypes.MessageUpdated,
            payload,
            userContext.UserId,
            ct).ConfigureAwait(false);
    }

    private static bool HasModel(string? providerId, string? modelId)
        => !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId);

    /// <summary>
    /// How a prompt goes in: a steer needs a harness that can take one, and only steers a turn that is running. When the
    /// turn ended before the prompt got here, the prompt starts a turn of its own, the way a queued one does, and isn't
    /// shown as having gone in mid-turn.
    /// </summary>
    private PromptDelivery? SteeringDelivery(PromptDelivery? requested, Session session, out FleetError? error)
    {
        error = null;
        if (requested != PromptDelivery.Steer)
            return requested;

        if (harnessRegistry.GetByType(session.HarnessType)?.Capabilities.SupportsSteering != true)
        {
            error = FleetError.ValidationError(
                "Prompt.Delivery",
                "This session's harness can't take a message while it works. Queue it instead: it's sent when the turn ends.");
            return requested;
        }

        return sessionActivityTracker.Get(session.Id)?.ActivityStatus
            is ActivityStatuses.Busy or ActivityStatuses.Retry or ActivityStatuses.Delegating
            ? PromptDelivery.Steer
            : PromptDelivery.Queue;
    }

    /// <summary>
    /// A prompt that names no agent or model gets the session's: the ones it started with or was last given. So
    /// "Default" in a session means what that session runs on, for people and automations alike.
    /// </summary>
    internal static PromptOptions? WithSessionChoices(PromptOptions? options, Session session)
    {
        var agent = string.IsNullOrWhiteSpace(options?.Agent) ? session.SelectedAgent : options.Agent;
        var namesModel = HasModel(options?.ProviderId, options?.ModelId);
        var useSessionModel = !namesModel && HasModel(session.SelectedProviderId, session.SelectedModelId);
        if (agent == options?.Agent && !useSessionModel)
            return options;

        var withChoices = (options ?? new PromptOptions()) with { Agent = agent };
        return useSessionModel
            ? withChoices with { ProviderId = session.SelectedProviderId, ModelId = session.SelectedModelId }
            : withChoices;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to send prompt to session {SessionId}")]
    private partial void LogPromptFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Unexpected failure sending prompt to session {SessionId}")]
    private partial void LogPromptUnexpectedFailure(Exception ex, string sessionId);
}
