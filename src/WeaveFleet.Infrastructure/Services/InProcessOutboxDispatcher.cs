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
            var messages = await outboxRepository.GetUndispatchedAsync(batchSize).ConfigureAwait(false);
            if (messages.Count == 0)
                return totalDispatched;

            foreach (var message in messages)
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(message.Payload);
                await broadcaster.BroadcastAsync(
                    message.Topic,
                    message.Type,
                    payload,
                    message.Id,
                    message.UserId,
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

    public void Dispose()
    {
        _signal.Dispose();
    }
}
