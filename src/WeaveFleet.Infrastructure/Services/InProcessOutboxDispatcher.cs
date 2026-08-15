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
        // Extract sessionId from topic (for session events) or payload
        var sessionId = ExtractSessionId(message.Topic, payload);

        // Look up session to get sourceReference
        string? sourceReference = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false);
            sourceReference = session?.SourceReference;
        }

        // Build a simple event summary
        var eventSummary = BuildEventSummary(message.Type, payload);

        // Notify automation dispatcher
        await automationNotifier.NotifyAsync(
            eventType: message.Type,
            eventId: message.Id.ToString(CultureInfo.InvariantCulture),
            sessionId: sessionId,
            sessionSourceReference: sourceReference,
            eventSummary: eventSummary,
            ct: cancellationToken).ConfigureAwait(false);
    }

    private static string? ExtractSessionId(string topic, JsonElement payload)
    {
        // For session events, the topic is typically the session ID
        // For other events, try to extract from payload
        if (!string.IsNullOrWhiteSpace(topic) && topic.StartsWith("session/", StringComparison.OrdinalIgnoreCase))
        {
            return topic["session/".Length..];
        }

        // Try to extract from payload
        if (payload.TryGetProperty("sessionId", out var sessionIdElement))
        {
            return sessionIdElement.GetString();
        }

        if (payload.TryGetProperty("payload", out var nestedPayload) &&
            nestedPayload.TryGetProperty("sessionId", out var nestedSessionIdElement))
        {
            return nestedSessionIdElement.GetString();
        }

        return null;
    }

    private static string? BuildEventSummary(string eventType, JsonElement payload)
    {
        // Build a simple human-readable summary based on event type
        return eventType switch
        {
            "session.started" => "Session started",
            "session.idled" => "Session became idle",
            "session.stopped" => "Session stopped",
            "session.deleted" => "Session deleted",
            "message.created" => "Message created",
            "message.updated" => "Message updated",
            _ => null
        };
    }

    public void Dispose()
    {
        _signal.Dispose();
    }
}
