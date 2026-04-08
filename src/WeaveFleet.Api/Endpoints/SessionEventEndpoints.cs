using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// SSE endpoints for per-session event streaming and global activity streams.
/// </summary>
public static class SessionEventEndpoints
{
    private static readonly string[] ActivityStreamTopics = ["sessions", "instances", "activity"];
    public static WebApplication MapSessionEventEndpoints(this WebApplication app)
    {
        // GET /api/sessions/{id}/events — SSE stream of harness events for one session
        app.MapGet("/api/sessions/{id}/events", async (
            string id,
            ISessionRepository sessionRepo,
            IEventBroadcaster broadcaster,
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
                var stoppedData = JsonSerializer.Serialize(new { sessionId = id, status = "stopped" });
                await WriteSseEventAsync(context.Response, "session_status", stoppedData, ct);
                return Results.Empty;
            }

            await foreach (var evt in broadcaster.SubscribeAsync([$"session:{id}"], ct))
            {
                var data = JsonSerializer.Serialize(new
                {
                    sessionId = id,
                    type = evt.Type,
                    payload = evt.Payload,
                    timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
                });
                await WriteSseEventAsync(context.Response, evt.Type, data, ct);
            }

            return Results.Empty;
        })
        .WithName("GetSessionEvents")
        .WithTags("Sessions");

        // GET /api/activity-stream — global SSE stream for dashboard events
        app.MapGet("/api/activity-stream", async (
            IEventBroadcaster broadcaster,
            HttpContext context,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var topics = ActivityStreamTopics;
            await foreach (var evt in broadcaster.SubscribeAsync(topics, ct))
            {
                var data = JsonSerializer.Serialize(new
                {
                    topic = evt.Topic,
                    type = evt.Type,
                    payload = evt.Payload,
                    timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
                });
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
