using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Services;

public sealed partial class InProcessOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IEventBroadcaster broadcaster,
    IAutomationEventNotifier automationNotifier,
    FleetOptions options,
    ILogger<InProcessOutboxDispatcher> logger) : IOutboxDispatcher, IDisposable
{
    private readonly AsyncAutoResetEvent _signal = new();

    public Task NotifyNewMessagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _signal.Set();
        return Task.CompletedTask;
    }

    public async Task<int> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        var totalDispatched = 0;
        var batchSize = Math.Max(1, options.Outbox.DispatchBatchSize);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var messages = await outboxRepository.GetUndispatchedAsync(batchSize).ConfigureAwait(false);
            if (messages.Count == 0)
                return totalDispatched;

            foreach (var message in messages)
            {
                var payload = JsonDocument.Parse(message.Payload).RootElement;
                await broadcaster.BroadcastAsync(
                    message.Topic,
                    message.Type,
                    payload,
                    message.Id,
                    message.UserId,
                    cancellationToken).ConfigureAwait(false);

                // Notify automation dispatcher
                await NotifyAutomationDispatcherAsync(
                    message,
                    payload,
                    sessionRepository,
                    cancellationToken).ConfigureAwait(false);
            }

            await outboxRepository.MarkDispatchedAsync(
                messages.Select(message => message.Id).ToArray(),
                DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);

            totalDispatched += messages.Count;
            LogDispatchBatch(messages.Count);

            if (messages.Count < batchSize)
                return totalDispatched;
        }

        return totalDispatched;
    }

    public async Task<bool> WaitForSignalAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, options.Outbox.PollIntervalMilliseconds)));
            await _signal.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispatched outbox batch of {Count} message(s).")]
    private partial void LogDispatchBatch(int count);

    private async Task NotifyAutomationDispatcherAsync(
        Domain.Entities.OutboxMessage message,
        JsonElement payload,
        ISessionRepository sessionRepository,
        CancellationToken cancellationToken)
    {
        // A sub agent's session is announced as a delegation; "a session starts" means one a person can see.
        if (IsSubAgentSessionCreated(message.Type, payload))
            return;

        var sessionId = ExtractSessionId(message.Topic, payload);
        var sourceReference = await ResolveSourceReferenceAsync(sessionRepository, sessionId).ConfigureAwait(false);

        await automationNotifier.NotifyAsync(
            eventType: message.Type,
            eventId: message.Id.ToString(CultureInfo.InvariantCulture),
            sessionId: sessionId,
            sessionSourceReference: sourceReference,
            eventSummary: BuildEventSummary(message.Type, payload),
            ct: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many parents to climb looking for an automation, so a broken parent chain can't loop.</summary>
    private const int MaxParentDepth = 10;

    /// <summary>
    /// The session an outbox message is about. Session lifecycle messages carry it as <c>sessionId</c>;
    /// delegation messages are published on <c>session:{parentId}</c> and carry <c>parentSessionId</c>.
    /// </summary>
    internal static string? ExtractSessionId(string topic, JsonElement payload)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["session:", "session/"])
        {
            if (topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && topic.Length > prefix.Length)
                return topic[prefix.Length..];
        }

        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetString(payload, "sessionId") is { } sessionId)
            return sessionId;

        if (TryGetString(payload, "parentSessionId") is { } parentSessionId)
            return parentSessionId;

        if (payload.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return TryGetString(nested, "sessionId");

        return null;
    }

    /// <summary>
    /// The source reference that decides whether an event came from an automation's own work. A sub agent's
    /// session has no source of its own, so this climbs to the parents: the first <c>automation:</c> reference
    /// wins, which stops an automation that starts sub agents from triggering itself.
    /// </summary>
    internal static async Task<string?> ResolveSourceReferenceAsync(ISessionRepository sessions, string? sessionId)
    {
        string? ownReference = null;
        var currentId = sessionId;
        for (var depth = 0; depth < MaxParentDepth && !string.IsNullOrWhiteSpace(currentId); depth++)
        {
            var session = await sessions.GetByIdAsync(currentId).ConfigureAwait(false);
            if (session is null)
                break;

            if (depth == 0)
                ownReference = session.SourceReference;

            if (session.SourceReference?.StartsWith("automation:", StringComparison.OrdinalIgnoreCase) == true)
                return session.SourceReference;

            currentId = session.ParentSessionId;
        }

        return ownReference;
    }

    internal static bool IsSubAgentSessionCreated(string eventType, JsonElement payload) =>
        eventType == "session_created"
        && payload.ValueKind == JsonValueKind.Object
        && TryGetString(payload, "parentSessionId") is not null;

    /// <summary>The line the automation's prompt gets under [Context], for the event types that reach it.</summary>
    internal static string? BuildEventSummary(string eventType, JsonElement payload)
    {
        var title = payload.ValueKind == JsonValueKind.Object ? TryGetString(payload, "title") : null;
        var status = payload.ValueKind == JsonValueKind.Object ? TryGetString(payload, "status") : null;
        return eventType switch
        {
            "session_created" => title is null ? "A session started" : $"A session started: {title}",
            "session_archived" => "A session was archived",
            "session_deleted" => "A session was deleted",
            "delegation.created" => title is null ? "A sub agent started" : $"A sub agent started: {title}",
            "delegation.updated" => (title, status) switch
            {
                (not null, not null) => $"A sub agent is {status}: {title}",
                (not null, null) => $"A sub agent changed: {title}",
                _ => "A sub agent changed"
            },
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        _signal.Dispose();
    }
}
