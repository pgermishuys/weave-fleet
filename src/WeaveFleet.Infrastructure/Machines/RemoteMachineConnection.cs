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
/// <c>session_notification</c>. Reconnects with backoff (2 s, doubling to 60 s) while the machine is away; stops when
/// the machine turns the token away, until the token changes. Reports what it sees through <c>onStatus</c>.
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
    private readonly HttpMessageHandler? _handler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public RemoteMachineConnection(
        RemoteMachine machine,
        string token,
        Func<SessionNotificationPayload, Task> onNotification,
        Func<string, bool, Task> onStatus,
        Func<Task> onConnected,
        ILogger logger,
        HttpMessageHandler? handler = null)
    {
        _machine = machine;
        _token = token;
        _onNotification = onNotification;
        _onStatus = onStatus;
        _onConnected = onConnected;
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
                await closed.Task.WaitAsync(stopping);
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
