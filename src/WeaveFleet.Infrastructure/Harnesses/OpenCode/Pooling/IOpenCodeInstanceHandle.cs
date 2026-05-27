namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Represents the OpenCode process/client ownership boundary used by a harness session.
/// </summary>
internal interface IOpenCodeInstanceHandle : IAsyncDisposable
{
    event EventHandler<int>? ProcessExited;

    OpenCodeHttpClient HttpClient { get; }

    int? ProcessId { get; }

    bool IsRunning { get; }

    Task EnsureConnectedAsync(CancellationToken ct);

    Task WaitForEventSubscriptionAsync(string openCodeSessionId, CancellationToken ct);

    Task SendCommandAsync(string openCodeSessionId, OpenCodeCommandRequest request, CancellationToken ct);

    IAsyncEnumerable<OpenCodeSseEvent> SubscribeEvents(string? openCodeSessionId, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}
