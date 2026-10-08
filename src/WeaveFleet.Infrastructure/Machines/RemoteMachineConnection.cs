using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// One live connection to another machine's event hub, listening on its global <c>sessions</c> topic for
/// <c>session_notification</c>, and on the topic of each session there that this Fleet follows (a session here waits on
/// it: agent hand-off) for that session's replies, failures and going idle. Reconnects with backoff (2 s, doubling to
/// 60 s) while the machine is away, following the same sessions again; stops when the machine turns the token away,
/// until the token changes. Reports what it sees through <c>onStatus</c>.
/// </summary>
internal sealed partial class RemoteMachineConnection : IAsyncDisposable
{
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(60);

    private readonly RemoteMachine _machine;
    private readonly string _token;
    private readonly Func<SessionNotificationPayload, Task> _onNotification;
    private readonly Func<string, bool, Task> _onStatus;
    private readonly Func<Task> _onConnected;
    private readonly Func<IReadOnlyCollection<string>> _followed;
    private readonly Func<string, DomainEvent, Task> _onSessionEvent;
    private readonly Func<string, Task> _onFollowing;
    private readonly HttpMessageHandler? _handler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private HubConnection? _live;

    public RemoteMachineConnection(
        RemoteMachine machine,
        string token,
        Func<SessionNotificationPayload, Task> onNotification,
        Func<string, bool, Task> onStatus,
        Func<Task> onConnected,
        ILogger logger,
        HttpMessageHandler? handler = null,
        Func<IReadOnlyCollection<string>>? followed = null,
        Func<string, DomainEvent, Task>? onSessionEvent = null,
        Func<string, Task>? onFollowing = null)
    {
        _machine = machine;
        _token = token;
        _onNotification = onNotification;
        _onStatus = onStatus;
        _onConnected = onConnected;
        _followed = followed ?? (() => []);
        _onSessionEvent = onSessionEvent ?? ((_, _) => Task.CompletedTask);
        _onFollowing = onFollowing ?? (_ => Task.CompletedTask);
        _logger = logger;
        _handler = handler;
    }

    public RemoteMachine Machine => _machine;

    public void Start() => _run = Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken stopping)
    {
        var delay = FirstRetry;
        while (!stopping.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hub = Build(closed);
            try
            {
                await hub.StartAsync(stopping);
                await hub.InvokeAsync("SubscribeToSessionsTopicAsync", stopping);
                await _onStatus(RemoteMachineStatuses.Online, true);
                LogConnected(_logger, _machine.Name);
                delay = FirstRetry;
                await _onConnected();
                _live = hub;
                // Events missed while away are asked about once each session's events flow again.
                foreach (var sessionId in _followed())
                    await SubscribeAsync(hub, sessionId, stopping);
                await closed.Task.WaitAsync(stopping);
                _live = null;
                await _onStatus(RemoteMachineStatuses.Unreachable, false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Someone replaced the token there. Retrying won't help until the token here changes.
                await _onStatus(RemoteMachineStatuses.Unauthorized, false);
                LogUnauthorized(_logger, _machine.Name);
                return;
            }
            catch (Exception ex)
            {
                _live = null;
                await _onStatus(RemoteMachineStatuses.Unreachable, false);
                LogUnreachable(_logger, _machine.Name, ex.Message);
            }

            try
            {
                await Task.Delay(delay, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestRetry.Ticks));
        }
    }

    /// <summary>
    /// Starts listening to a session there now, when connected; otherwise it's followed once the connection is back
    /// (<c>followed</c> already names it).
    /// </summary>
    public async Task FollowAsync(string sessionId)
    {
        if (_live is { } hub)
            await SubscribeAsync(hub, sessionId, _stop.Token);
    }

    /// <summary>Stops listening to a session there.</summary>
    public async Task UnfollowAsync(string sessionId)
    {
        if (_live is not { } hub)
            return;
        try
        {
            await hub.SendAsync("UnsubscribeFromSessionAsync", sessionId, _stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // It goes with the connection anyway.
        }
    }

    /// <summary>
    /// Subscribes to the session's topic and waits for it to take, so that from then on its events arrive; then asks
    /// about anything that happened before (<c>onFollowing</c>). Its snapshot isn't needed.
    /// </summary>
    private async Task SubscribeAsync(HubConnection hub, string sessionId, CancellationToken ct)
    {
        try
        {
            await hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFollowFailed(_logger, sessionId, _machine.Name, ex.Message);
            return;
        }

        await _onFollowing(sessionId);
    }

    private HubConnection Build(TaskCompletionSource closed)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_machine.BaseUrl}/hubs/session-events", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
                if (_handler is not null)
                {
                    options.HttpMessageHandlerFactory = _ => _handler;
                    options.Transports = HttpTransportType.LongPolling;
                }
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, RemoteHubJsonContext.Default))
            .Build();

        hub.On("Event", [typeof(string), typeof(long), typeof(JsonElement)], args => OnEventAsync((string?)args[0], args[2] is JsonElement data ? data : default));
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return hub;
    }

    private async Task OnEventAsync(string? topic, JsonElement data)
    {
        if (topic is not null && topic.StartsWith("session:", StringComparison.Ordinal))
        {
            var sessionId = topic["session:".Length..];
            if (ToDomainEvent(sessionId, data) is { } domainEvent)
                await _onSessionEvent(sessionId, domainEvent);
            return;
        }

        if (topic != "sessions" || data.ValueKind != JsonValueKind.Object)
            return;
        if (!data.TryGetProperty("type", out var type) || type.GetString() != "session_notification")
            return;
        if (!data.TryGetProperty("properties", out var properties))
            return;

        SessionNotificationPayload? payload;
        try
        {
            payload = properties.Deserialize(ApplicationJsonContext.Default.SessionNotificationPayload);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null)
            return;

        await _onNotification(payload);
    }

    /// <summary>
    /// The events of a followed session that say how a turn answering a message went, read back from the hub's wire
    /// form (<c>{ type, properties }</c>, the domain event's own JSON): a reply (with its parent), a failed turn, idle.
    /// Anything else, or anything it can't read, is null.
    /// </summary>
    internal static DomainEvent? ToDomainEvent(string sessionId, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("type", out var type))
            return null;
        var properties = data.TryGetProperty("properties", out var found) ? found : default;

        try
        {
            return type.GetString() switch
            {
                "message.created" when properties.ValueKind == JsonValueKind.Object
                    => properties.Deserialize(InfrastructureJsonContext.Default.MessageLifecyclePayload) is { } created ? new MessageCreated { Payload = created } : null,
                "message.updated" when properties.ValueKind == JsonValueKind.Object
                    => properties.Deserialize(InfrastructureJsonContext.Default.MessageLifecyclePayload) is { } updated ? new MessageUpdated { Payload = updated } : null,
                "turn.failed" when properties.ValueKind == JsonValueKind.Object
                    => properties.Deserialize(InfrastructureJsonContext.Default.TurnFailedPayload) is { } failed ? new TurnFailed { Payload = failed } : null,
                "session.idled" => new SessionIdled { Payload = new SessionIdledPayload { SessionId = sessionId } },
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // It stops on its own.
            }
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't follow session {SessionId} on {Machine}: {Reason}.")]
    private static partial void LogFollowFailed(ILogger logger, string sessionId, string machine, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening to {Machine} for notifications.")]
    private static partial void LogConnected(ILogger logger, string machine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Can't reach {Machine}: {Reason}. Trying again shortly.")]
    private static partial void LogUnreachable(ILogger logger, string machine, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Machine} turned this Fleet's token away; update it in Settings › Machines.")]
    private static partial void LogUnauthorized(ILogger logger, string machine);
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class RemoteHubJsonContext : JsonSerializerContext
{
}
