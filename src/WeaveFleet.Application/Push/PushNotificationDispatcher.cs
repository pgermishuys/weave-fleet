using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Push;

/// <summary>
/// Pushes session notifications to phones: this machine's own (as an <see cref="ISessionNotificationSink"/>) and,
/// on a phone's home machine, other machines' (<see cref="Enqueue"/> from the remote watcher). Each subscription
/// gets the kinds it chose; one that asked to be quiet at the desk is skipped while a computer has Fleet on screen.
/// <para>
/// Notifications queue up and one worker sends them, so the relay never waits on a push service. A subscription the
/// push service says is gone is deleted; one that fails <see cref="MaxFailures"/> times in a row is too.
/// </para>
/// </summary>
public sealed partial class PushNotificationDispatcher : ISessionNotificationSink, IDisposable
{
    public const int MaxFailures = 10;
    private const int QueueLength = 256;

    private readonly IPushSubscriptionRepository _subscriptions;
    private readonly IPushSender[] _senders;
    private readonly DeskPresenceTracker _presence;
    private readonly MachineIdentityStore? _machine;
    private readonly TimeProvider _time;
    private readonly ILogger<PushNotificationDispatcher> _logger;
    private readonly Channel<SessionNotificationPayload> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;

    public PushNotificationDispatcher(
        IPushSubscriptionRepository subscriptions,
        IEnumerable<IPushSender> senders,
        DeskPresenceTracker presence,
        TimeProvider time,
        ILogger<PushNotificationDispatcher> logger,
        MachineIdentityStore? machine = null)
    {
        _subscriptions = subscriptions;
        _senders = [.. senders];
        _presence = presence;
        _machine = machine;
        _time = time;
        _logger = logger;
        _queue = Channel.CreateBounded<SessionNotificationPayload>(new BoundedChannelOptions(QueueLength)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _worker = Task.Run(WorkAsync);
    }

    public Task HandleAsync(SessionNotificationPayload payload, string userId, CancellationToken cancellationToken)
    {
        Enqueue(payload);
        return Task.CompletedTask;
    }

    /// <summary>Queues a notification to push. One without a machine id is this machine's.</summary>
    public void Enqueue(SessionNotificationPayload payload) => _queue.Writer.TryWrite(payload);

    /// <summary>Pushes one notification to every subscription that wants it, and returns once each was tried.</summary>
    public async Task SendAsync(SessionNotificationPayload notification, CancellationToken cancellationToken)
    {
        var kind = notification.Kind ?? KindFromReason(notification.Reason);
        var all = await _subscriptions.ListAsync().ConfigureAwait(false);
        var wanting = all.Where(s => s.Kinds.Contains(kind)).ToList();
        if (wanting.Count == 0)
            return;

        var atDesk = _presence.AnyDesktopVisible;
        var payload = ToPushPayload(notification, kind);
        var message = new PushMessage(payload.ToJson(), Urgent: kind is not SessionNotificationKinds.Finished, PushMessage.DefaultTimeToLive);

        foreach (var subscription in wanting)
        {
            if (subscription.QuietWhenDesk && atDesk)
                continue;

            var sender = _senders.LastOrDefault(s => s.Channel == subscription.Channel);
            if (sender is null)
                continue;

            var result = await sender.SendAsync(subscription, message, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PushSendOutcome.Delivered:
                    await _subscriptions.RecordSuccessAsync(subscription.Id, _time.GetUtcNow()).ConfigureAwait(false);
                    break;
                case PushSendOutcome.Gone:
                    await _subscriptions.DeleteByEndpointAsync(subscription.Endpoint).ConfigureAwait(false);
                    LogGone(_logger, subscription.Id);
                    break;
                default:
                    if (await _subscriptions.RecordFailureAsync(subscription.Id).ConfigureAwait(false) >= MaxFailures)
                    {
                        await _subscriptions.DeleteByEndpointAsync(subscription.Endpoint).ConfigureAwait(false);
                        LogGaveUp(_logger, subscription.Id, MaxFailures);
                    }

                    break;
            }
        }
    }

    /// <summary>A notification from a Fleet too old to say its kind: needs-you reads as a permission ask.</summary>
    public static string KindFromReason(string reason) => reason switch
    {
        SessionNotificationReasons.NeedsYou => SessionNotificationKinds.Permission,
        SessionNotificationReasons.Failed => SessionNotificationKinds.Failed,
        _ => SessionNotificationKinds.Finished,
    };

    private PushPayload ToPushPayload(SessionNotificationPayload notification, string kind)
    {
        var identity = _machine?.Get();
        var machineId = notification.MachineId ?? identity?.Id ?? "local";
        var machineName = notification.MachineName ?? identity?.Name ?? Environment.MachineName;
        var requestId = kind == SessionNotificationKinds.Permission ? notification.RequestId : null;
        return new PushPayload(
            1,
            machineId,
            machineName,
            notification.SessionId,
            kind,
            notification.Reason,
            PushPayload.Clip(notification.Title, PushPayload.MaxTitleLength),
            PushPayload.Clip(notification.Body, PushPayload.MaxBodyLength),
            PushPayload.UrlFor(machineId, notification.SessionId, requestId),
            $"{machineId}:{notification.SessionId}",
            requestId);
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(notification, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogFailed(_logger, ex, notification.SessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    // Registered both as itself and as a sink, so the container disposes it twice.
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The worker ends by cancellation.
        }

        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push subscription {Id} is gone; removed it.")]
    private static partial void LogGone(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push subscription {Id} failed {Count} times in a row; removed it.")]
    private static partial void LogGaveUp(ILogger logger, string id, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't push the notification about session {SessionId}.")]
    private static partial void LogFailed(ILogger logger, Exception ex, string sessionId);
}
