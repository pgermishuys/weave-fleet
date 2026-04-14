namespace WeaveFleet.Application.Services;

/// <summary>
/// Outward-facing abstraction for waking or dispatching the committed outbox.
/// </summary>
public interface IOutboxDispatcher
{
    Task NotifyNewMessagesAsync(CancellationToken cancellationToken);
}
