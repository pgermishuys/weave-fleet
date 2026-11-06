using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// One live connection to another machine's event hub, listening on its global <c>sessions</c> topic for
/// <c>session_notification</c>, and on the topic of each session there that this Fleet follows (a session here waits on
/// it: agent hand-off) for that session's replies, failures and going idle. Reconnects with backoff (2 s, doubling to
/// 60 s) while the machine is away, following the same sessions again; stops when the machine turns the token away,
/// until the token changes. Reports what it sees through <c>onStatus</c>. Each connect starts on the address
/// <see cref="MachineRoutes"/> picks, and a connection on a lower address moves up once a better one holds.
/// </summary>
internal sealed partial class RemoteMachineConnection : IAsyncDisposable
{
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(60);

    private readonly RemoteMachine _machine;
    private readonly string _token;
    private readonly Func<SessionNotificationPayload, Task> _onNotification;
    private readonly Func<string, bool, MachineDiagnosis?, Task> _onStatus;
    private readonly Func<Task> _onConnected;
    private readonly Func<IReadOnlyCollection<string>> _followed;
    private readonly Func<string, DomainEvent, Task> _onSessionEvent;
    private readonly Func<string, Task> _onFollowing;
    private readonly HttpMessageHandler? _handler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly MachineRoutes? _routes;
    private readonly TimeProvider _time;
    private Task? _run;
    private HubConnection? _live;

    // The connection whose events count. While moving to a better address both are open for a moment; only one is heard.
    private HubConnection? _receiving;

    /// <summary>For tests: how often a connection looks for a better address.</summary>
    internal TimeSpan RecheckEvery { get; init; } = MachineRoutes.RecheckEvery;

    public RemoteMachineConnection(
        RemoteMachine machine,
        string token,
        Func<SessionNotificationPayload, Task> onNotification,
        Func<string, bool, MachineDiagnosis?, Task> onStatus,
        Func<Task> onConnected,
        ILogger logger,
        HttpMessageHandler? handler = null,
        Func<IReadOnlyCollection<string>>? followed = null,
        Func<string, DomainEvent, Task>? onSessionEvent = null,
        Func<string, Task>? onFollowing = null,
        MachineRoutes? routes = null,
        TimeProvider? time = null)
    {
        _routes = routes;
        _time = time ?? TimeProvider.System;
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
            var url = _machine.BaseUrl;
            HubConnection? hub = null;
            try
            {
                if (_routes is not null)
                    url = await _routes.PickAsync(_machine.Id, stopping) is { Length: > 0 } picked ? picked : url;
                hub = Build(closed, url);
                _receiving = hub;
                await hub.StartAsync(stopping);
                await hub.InvokeAsync("SubscribeToSessionsTopicAsync", stopping);
                await _onStatus(RemoteMachineStatuses.Online, true, null);
                LogConnected(_logger, _machine.Name, url);
                delay = FirstRetry;
                await _onConnected();
                _live = hub;
                // Events missed while away are asked about once each session's events flow again.
                foreach (var sessionId in _followed())
                    await SubscribeAsync(hub, sessionId, stopping);
                (hub, closed, url) = await StayAsync(hub, closed, url, stopping);
                _live = null;
                await _onStatus(RemoteMachineStatuses.Unreachable, false, MachineDiagnoses.ConnectionLost(url, _machine.Name).Diagnosis);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Someone replaced the token there. Retrying won't help until the token here changes.
                await _onStatus(RemoteMachineStatuses.Unauthorized, false, MachineDiagnoses.FromStatus(HttpStatusCode.Unauthorized, url, _machine.Name).Diagnosis);
                LogUnauthorized(_logger, _machine.Name);
                return;
            }
            catch (Exception ex)
            {
                _live = null;
                await _onStatus(RemoteMachineStatuses.Unreachable, false, MachineDiagnoses.FromException(ex, url, _machine.Name).Diagnosis);
                LogUnreachable(_logger, _machine.Name, ex.Message);
            }
            finally
            {
                if (hub is not null)
                    await hub.DisposeAsync();
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
    /// Keeps the connection until it closes, and returns the one in use then. Meanwhile, every
    /// <see cref="MachineRoutes.RecheckEvery"/>, it asks the machine for its addresses and, while on a lower one, looks
    /// for a better one that answers again. It moves only once a connection there is up and following everything this
    /// one follows; then the old one closes. A better address that answers but can't hold a connection is left alone.
    /// </summary>
    private async Task<(HubConnection Hub, TaskCompletionSource Closed, string Url)> StayAsync(
        HubConnection hub, TaskCompletionSource closed, string url, CancellationToken stopping)
    {
        if (_routes is null)
        {
            await closed.Task.WaitAsync(stopping);
            return (hub, closed, url);
        }

        await LearnAsync(url, stopping);
        while (true)
        {
            var tick = Task.Delay(RecheckEvery, _time, stopping);
            if (await Task.WhenAny(closed.Task, tick) == closed.Task)
                return (hub, closed, url);
            await tick;

            await LearnAsync(url, stopping);
            if (await _routes.IsBestAsync(_machine.Id, url) || await _routes.BetterAsync(_machine.Id, url, stopping) is not { } better)
                continue;

            var nextClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = Build(nextClosed, better);
            try
            {
                await next.StartAsync(stopping);
                await next.InvokeAsync("SubscribeToSessionsTopicAsync", stopping);
                foreach (var sessionId in _followed())
                    await next.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId, stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
            {
                LogStayed(_logger, _machine.Name, better, url, ex.Message);
                await next.DisposeAsync();
                continue;
            }

            // From here the new connection's events count and the old one's don't, so none is missed or doubled.
            _receiving = next;
            _live = next;
            var old = hub;
            (hub, closed, url) = (next, nextClosed, better);
            await _routes.UseAsync(_machine.Id, better);
            LogMoved(_logger, _machine.Name, better);
            await old.DisposeAsync();
        }
    }

    private async Task LearnAsync(string url, CancellationToken stopping)
    {
        try
        {
            await _routes!.LearnAsync(_machine.Id, url, _token, stopping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The list stays as it was until the next time.
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

    private HubConnection Build(TaskCompletionSource closed, string url)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{url}/hubs/session-events", options =>
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

        hub.On("Event", [typeof(string), typeof(long), typeof(JsonElement)], args =>
            ReferenceEquals(_receiving, hub) ? OnEventAsync((string?)args[0], args[2] is JsonElement data ? data : default) : Task.CompletedTask);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening to {Machine} for notifications at {Url}.")]
    private static partial void LogConnected(ILogger logger, string machine, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved to {Machine}'s better address {Url}.")]
    private static partial void LogMoved(ILogger logger, string machine, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Machine} answered at {Better} but a connection there didn't hold ({Reason}); staying on {Url}.")]
    private static partial void LogStayed(ILogger logger, string machine, string better, string url, string reason);

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
