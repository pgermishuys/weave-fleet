using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>What the user queues: the text as typed, how it goes out, and what the composer had picked.</summary>
public sealed record QueuePromptRequest(
    string? Text,
    string? Kind,
    string? Command = null,
    string? Arguments = null,
    string? Agent = null,
    string? ProviderId = null,
    string? ModelId = null,
    string? Effort = null);

/// <summary>A queued message as the client shows it.</summary>
public sealed record QueuedPromptView(string Id, string Kind, string Text, string CreatedAt)
{
    public static QueuedPromptView From(QueuedPrompt item)
        => new(item.Id, item.Kind, item.Text, item.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>The <c>session.queue</c> event: a session's whole queue, sent whenever it changes.</summary>
public sealed record SessionQueueChanged(string SessionId, IReadOnlyList<QueuedPromptView> Items);

/// <summary>
/// The messages the user queued while a session's agent works. They're kept in Fleet's database, not the browser,
/// so leaving the session, reloading or closing the tab loses nothing. When the turn ends, Fleet sends the first
/// (<see cref="PromptQueueDispatcher"/>), and the next when that turn ends. Every change is broadcast as
/// <see cref="ChangedEvent"/> on the session's topic, so every open client shows the same queue.
/// </summary>
public sealed partial class PromptQueueService(
    SessionOrchestrator orchestrator,
    ISessionRepository sessions,
    IQueuedPromptRepository queue,
    IEventBroadcaster broadcaster,
    SessionActivityTracker activity,
    IHarnessRegistry harnesses,
    IUserContext userContext,
    ILogger<PromptQueueService> logger)
{
    /// <summary>The event carrying a session's queue after it changed (<see cref="SessionQueueChanged"/>).</summary>
    public const string ChangedEvent = "session.queue";

    /// <summary>The longest message Fleet queues, as for a prompt.</summary>
    public const int MaxTextLength = 100_000;

    public async Task<Result<IReadOnlyList<QueuedPrompt>>> ListAsync(string sessionId)
    {
        if (await sessions.GetByIdAsync(sessionId).ConfigureAwait(false) is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);
        return Result.Success(await queue.ListAsync(sessionId).ConfigureAwait(false));
    }

    /// <summary>
    /// Adds a message to the end of the session's queue. When the session isn't in a turn after all (it ended while
    /// the request was on its way), the queue is sent at once rather than waiting for a turn that won't end.
    /// </summary>
    public async Task<Result<QueuedPrompt>> EnqueueAsync(string sessionId, QueuePromptRequest request, CancellationToken ct = default)
    {
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? QueuedPromptKinds.Prompt : request.Kind;
        if (!QueuedPromptKinds.IsKnown(kind))
            return FleetError.ValidationError("Queue.Kind", "kind must be \"prompt\", \"command\" or \"shell\".");
        if (string.IsNullOrWhiteSpace(request.Text))
            return FleetError.ValidationError("Queue.Text", "Type a message to queue.");
        if (request.Text.Length > MaxTextLength)
            return FleetError.ValidationError("Queue.Text", $"The message is longer than {MaxTextLength} characters.");
        if (kind == QueuedPromptKinds.Command && string.IsNullOrWhiteSpace(request.Command))
            return FleetError.ValidationError("Queue.Command", "A queued command needs its name.");

        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);
        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");
        if (kind == QueuedPromptKinds.Shell && harnesses.GetByType(session.HarnessType)?.Capabilities.SupportsShellCommands != true)
            return FleetError.ValidationError("Queue.Kind", "This session's harness can't run shell commands.");

        var item = new QueuedPrompt
        {
            Id = $"queued_{Guid.NewGuid():N}",
            SessionId = sessionId,
            Kind = kind,
            Text = request.Text,
            Command = request.Command,
            Arguments = request.Arguments,
            Agent = request.Agent,
            ProviderId = request.ProviderId,
            ModelId = request.ModelId,
            Effort = request.Effort,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (!await queue.AddAsync(item).ConfigureAwait(false))
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        await BroadcastAsync(sessionId, ct).ConfigureAwait(false);

        if (!IsInTurn(sessionId))
            await SendNextAsync(sessionId, ct).ConfigureAwait(false);

        return item;
    }

    public async Task<Result<Unit>> RemoveAsync(string sessionId, string itemId, CancellationToken ct = default)
    {
        if (await queue.TakeAsync(sessionId, itemId).ConfigureAwait(false) is null)
            return FleetError.NotFoundFor(nameof(QueuedPrompt), itemId);

        await BroadcastAsync(sessionId, ct).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>
    /// Sends one queued item now instead of waiting its turn. While a turn runs, a message goes into it (a steer),
    /// where the harness can take one; a command can't go into a turn. When the session is idle, it's sent as usual.
    /// </summary>
    public async Task<Result<Unit>> SendNowAsync(string sessionId, string itemId, CancellationToken ct = default)
    {
        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        var inTurn = IsInTurn(sessionId);
        if (inTurn)
        {
            var listed = (await queue.ListAsync(sessionId).ConfigureAwait(false)).FirstOrDefault(i => i.Id == itemId);
            if (listed is null)
                return FleetError.NotFoundFor(nameof(QueuedPrompt), itemId);
            if (listed.Kind != QueuedPromptKinds.Prompt)
                return FleetError.ValidationError("Queue.Kind", "Only a message can go into a running turn. A command waits for the turn to end.");
            if (harnesses.GetByType(session.HarnessType)?.Capabilities.SupportsSteering != true)
                return FleetError.ValidationError("Prompt.Delivery", "This session's harness can't take a message while it works. It's sent when the turn ends.");
        }

        var item = await queue.TakeAsync(sessionId, itemId).ConfigureAwait(false);
        if (item is null)
            return FleetError.NotFoundFor(nameof(QueuedPrompt), itemId);

        await BroadcastAsync(sessionId, ct).ConfigureAwait(false);
        var sent = await SendAsync(item, inTurn ? PromptDelivery.Steer : PromptDelivery.Queue, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            await queue.ReturnToFrontAsync(item).ConfigureAwait(false);
            await BroadcastAsync(sessionId, ct).ConfigureAwait(false);
        }

        return sent;
    }

    /// <summary>
    /// Sends the first queued item: called when the session's turn ends, and after queueing into an idle session. A
    /// shell command isn't a turn, so the item after one goes straight away. An item the session refuses goes back
    /// to the front, for the next time the session is free.
    /// </summary>
    public async Task SendNextAsync(string sessionId, CancellationToken ct = default)
    {
        while (await queue.TakeFirstAsync(sessionId).ConfigureAwait(false) is { } item)
        {
            await BroadcastAsync(sessionId, ct).ConfigureAwait(false);
            var sent = await SendAsync(item, PromptDelivery.Queue, ct).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                LogSendFailed(sessionId, item.Id, sent.Error.Description);
                await queue.ReturnToFrontAsync(item).ConfigureAwait(false);
                await BroadcastAsync(sessionId, ct).ConfigureAwait(false);
                return;
            }

            if (item.Kind != QueuedPromptKinds.Shell)
                return;
        }
    }

    private Task<Result<Unit>> SendAsync(QueuedPrompt item, PromptDelivery delivery, CancellationToken ct) => item.Kind switch
    {
        QueuedPromptKinds.Command => orchestrator.CommandSessionAsync(
            item.SessionId,
            new CommandOptions
            {
                Command = item.Command!,
                Arguments = item.Arguments,
                Agent = item.Agent,
                ProviderId = item.ProviderId,
                ModelId = item.ModelId,
            },
            ct),
        QueuedPromptKinds.Shell => orchestrator.RunShellCommandAsync(item.SessionId, item.Text.TrimStart().TrimStart('!').Trim(), ct),
        _ => orchestrator.PromptSessionAsync(
            item.SessionId,
            item.Text,
            new PromptOptions
            {
                Agent = item.Agent,
                ProviderId = item.ProviderId,
                ModelId = item.ModelId,
                Effort = item.Effort,
                Delivery = delivery,
            },
            ct),
    };

    /// <summary>Busy, stopped on a question, or waiting on a delegated session: the turn hasn't ended.</summary>
    private bool IsInTurn(string sessionId)
    {
        var status = activity.Get(sessionId)?.ActivityStatus;
        return SessionActivityTracker.IsInTurn(status) || status == ActivityStatuses.Delegating;
    }

    private async Task BroadcastAsync(string sessionId, CancellationToken ct)
    {
        var items = await queue.ListAsync(sessionId).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(
            new SessionQueueChanged(sessionId, items.Select(QueuedPromptView.From).ToList()),
            ApplicationJsonContext.Default.SessionQueueChanged);
        await broadcaster.BroadcastAsync($"session:{sessionId}", ChangedEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send queued item {ItemId} to session {SessionId}: {Reason}. It stays first in the queue.")]
    private partial void LogSendFailed(string sessionId, string itemId, string reason);
}
