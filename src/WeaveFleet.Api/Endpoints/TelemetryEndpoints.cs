using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static partial class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/telemetry").WithTags("Telemetry");

        // POST /api/telemetry/actions — fire-and-forget UI action logging
        group.MapPost("/actions", (
            [FromBody] UiActionRequest request,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("WeaveFleet.Api.Telemetry.UiActions");

            if (logger.IsEnabled(LogLevel.Information))
            {
                var scopeData = new Dictionary<string, object>();

                if (request.SessionId is not null)
                    scopeData[FleetInstrumentation.SessionIdTag] = request.SessionId;

                if (request.Metadata.HasValue && request.Metadata.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in request.Metadata.Value.EnumerateObject())
                        scopeData[prop.Name] = prop.Value.ToString();
                }

                using (logger.BeginScope(scopeData))
                {
                    LogUiAction(logger, request.Action);
                }
            }

            return Results.NoContent();
        });

        return app;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "UI action: {Action}")]
    private static partial void LogUiAction(ILogger logger, string action);
}

internal sealed record UiActionRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

#pragma warning restore IL2026
