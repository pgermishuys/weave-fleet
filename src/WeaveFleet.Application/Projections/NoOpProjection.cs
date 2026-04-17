using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Projections;

/// <summary>
/// Logs every received event. Used during rollout to prove consumer wiring end-to-end before a
/// real projection takes over.
/// </summary>
public sealed class NoOpProjection : IProjection<HarnessEvent>
{
    private static readonly Action<ILogger, string, string, Exception?> LogReceived =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(1, "NoOpProjectionReceived"),
            "NoOpProjection received {EventType} on session {SessionId}");

    private readonly ILogger<NoOpProjection> _logger;

    public NoOpProjection(ILogger<NoOpProjection> logger) => _logger = logger;

    public string Name => "noop";

    public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
    {
        LogReceived(_logger, evt.Type, ctx.FleetSessionId, null);
        return Task.CompletedTask;
    }
}
