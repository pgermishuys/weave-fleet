using System.Text;
using System.Text.Json;
using WeaveFleet.Api;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// SSE endpoints for per-session event streaming and global activity streams.
/// </summary>
public static class SessionEventEndpoints
{
    private static readonly string[] ActivityStreamTopics = ["sessions", "instances", "activity"];
    public static IEndpointRouteBuilder MapSessionEventEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/sessions/{id}/events — SSE stream of harness events for one session
        app.MapGet("/api/sessions/{id}/events", async (
            string id,
            ISessionRepository sessionRepo,
            IEventBroadcaster broadcaster,
            IUserContext userContext,
            HttpContext context,
            CancellationToken ct) =>
        {
            var session = await sessionRepo.GetByIdAsync(id);
            if (session is null)
                return Results.NotFound(new { error = $"Session {id} not found." });

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            if (session.Status is "completed" or "failed" or "aborted")
            {
                // No live instance — send a single "stopped" event and close
                var stoppedData = JsonSerializer.Serialize(new SseSessionStoppedPayload(id, "stopped"), ApiJsonContext.Default.SseSessionStoppedPayload);
                await WriteSseEventAsync(context.Response, "session_status", stoppedData, ct);
                return Results.Empty;
            }

            // Subscribe with user scope so only events owned by this user are delivered
            await foreach (var evt in broadcaster.SubscribeAsync([$"session:{id}"], userContext.UserId, ct))
            {
                var sanitizedPayload = ClientPayloadSanitizer.SanitizeEventPayload(evt.Type, evt.Payload);
                if (!sanitizedPayload.HasValue)
                    continue;

                var data = JsonSerializer.Serialize(
                    new SseSessionEventPayload(id, evt.Type, sanitizedPayload.Value, evt.SequenceNumber, evt.Timestamp.ToUnixTimeMilliseconds()),
                    ApiJsonContext.Default.SseSessionEventPayload);
                await WriteSseEventAsync(context.Response, evt.Type, data, ct);
            }

            return Results.Empty;
        })
        .WithName("GetSessionEvents")
        .WithTags("Sessions");

        // GET /api/activity-stream — global SSE stream for dashboard events
        app.MapGet("/api/activity-stream", async (
            IEventBroadcaster broadcaster,
            IUserContext userContext,
            HttpContext context,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            // Subscribe with user scope — only events for this user are delivered
            await foreach (var evt in broadcaster.SubscribeAsync(ActivityStreamTopics, userContext.UserId, ct))
            {
                var sanitizedPayload = ClientPayloadSanitizer.SanitizeEventPayload(evt.Type, evt.Payload);
                if (!sanitizedPayload.HasValue)
                    continue;

                var data = JsonSerializer.Serialize(
                    new SseActivityEventPayload(evt.Topic, evt.Type, sanitizedPayload.Value, evt.SequenceNumber, evt.Timestamp.ToUnixTimeMilliseconds()),
                    ApiJsonContext.Default.SseActivityEventPayload);
                await WriteSseEventAsync(context.Response, evt.Type, data, ct);
            }

            return Results.Empty;
        })
        .WithName("GetActivityStream")
        .WithTags("Fleet");

        return app;
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventName,
        string data,
        CancellationToken ct)
    {
        var line = $"event: {eventName}\ndata: {data}\n\n";
        await response.WriteAsync(line, Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}
#pragma warning restore IL2026
