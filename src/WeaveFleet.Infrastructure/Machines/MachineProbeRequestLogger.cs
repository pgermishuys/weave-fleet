using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Infrastructure.Machines;

/// <summary>
/// Request logging for <see cref="MachineRoutes"/>' probes: one Debug line per probe, without a stack trace. A dead
/// address is probed every minute; <see cref="MachineRoutes"/> logs once at Information when it stops answering.
/// </summary>
internal sealed partial class MachineProbeRequestLogger(ILogger<MachineProbeRequestLogger> logger) : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request) => null;

    public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
        => LogAnswered(logger, request.RequestUri, (int)response.StatusCode, elapsed.TotalMilliseconds);

    public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
        => LogFailed(logger, request.RequestUri, elapsed.TotalMilliseconds, exception.Message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probe {Uri} answered {StatusCode} in {ElapsedMilliseconds:0} ms.")]
    private static partial void LogAnswered(ILogger logger, Uri? uri, int statusCode, double elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probe {Uri} failed after {ElapsedMilliseconds:0} ms: {Reason}")]
    private static partial void LogFailed(ILogger logger, Uri? uri, double elapsedMilliseconds, string reason);
}
