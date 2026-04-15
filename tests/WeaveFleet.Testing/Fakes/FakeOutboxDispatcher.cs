using WeaveFleet.Application.Services;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeOutboxDispatcher : IOutboxDispatcher
{
    public int NotificationCount { get; private set; }

    public Task NotifyNewMessagesAsync(CancellationToken cancellationToken)
    {
        NotificationCount++;
        return Task.CompletedTask;
    }
}
