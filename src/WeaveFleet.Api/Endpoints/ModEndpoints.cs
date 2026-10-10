using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Mods: the user's kept mods and versions, the drafts the agent is writing in a session, Keep and Undo, and "Start
/// without mods". With the Mods switch off every route answers 404, as if it didn't exist; safe mode doesn't turn
/// them off, so Settings can still turn mods back on.
/// </summary>
public static class ModEndpoints
{
    public static IEndpointRouteBuilder MapModEndpoints(this IEndpointRouteBuilder app)
    {
        // Configured statement by statement, with the gate as a method: the source generator that makes these endpoints
        // AOT-safe skips MapX calls on a group built in one chain ending in a filter lambda, and they crash the AOT build.
        var mods = app.MapGroup("/api/mods").WithTags("Mods");
        mods.AddEndpointFilter(OnlyWhenSwitchedOnAsync);

        mods.MapGet("/", async (ModService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .WithName("ListMods")
            .Produces<ModsView>();

        // PUT /api/mods/safe-mode  { "on": true } — "Start without mods": nothing runs until Fleet restarts or this is false.
        mods.MapPut("/safe-mode", async (SetModsSafeModeRequest request, ModService service, CancellationToken ct)
                => Results.Ok(await service.SetSafeModeAsync(request.On, ct)))
            .WithName("SetModsSafeMode")
            .Produces<ModsView>();

        mods.MapGet("/{name}", async (string name, ModService service, CancellationToken ct)
                => (await service.GetAsync(name, ct)).ToApiResult())
            .WithName("GetMod")
            .Produces<ModView>();

        mods.MapGet("/{name}/versions/{version:int}/files", async (string name, int version, ModService service, CancellationToken ct)
                => (await service.ReadVersionFilesAsync(name, version, ct)).ToApiResult())
            .WithName("GetModVersionFiles")
            .Produces<ModFilesView>();

        // PUT /api/mods/{name}/active  { "version": 2 } — makes it the version sessions load, and turns the mod on.
        mods.MapPut("/{name}/active", async (string name, UseModVersionRequest request, ModService service, CancellationToken ct)
                => (await service.UseVersionAsync(name, request.Version, ct)).ToApiResult())
            .WithName("UseModVersion")
            .Produces<ModView>();

        // POST /api/mods/{name}/undo — the previous version; on the first, turns the mod off. Nothing is deleted.
        mods.MapPost("/{name}/undo", async (string name, ModService service, CancellationToken ct)
                => (await service.UndoAsync(name, ct)).ToApiResult())
            .WithName("UndoMod")
            .Produces<ModView>();

        mods.MapPost("/{name}/on", async (string name, ModService service, CancellationToken ct)
                => (await service.SetOnAsync(name, on: true, ct)).ToApiResult())
            .WithName("TurnModOn")
            .Produces<ModView>();

        mods.MapPost("/{name}/off", async (string name, ModService service, CancellationToken ct)
                => (await service.SetOnAsync(name, on: false, ct)).ToApiResult())
            .WithName("TurnModOff")
            .Produces<ModView>();

        // ── A session's drafts ───────────────────────────────────────────────
        // Drafts live in the user's folder under the session's id, so another user's session has none to show.

        var drafts = app.MapGroup("/api/sessions/{sessionId}/mods").WithTags("Mods");
        drafts.AddEndpointFilter(OnlyWhenSwitchedOnAsync);

        drafts.MapGet("/drafts", async (string sessionId, ModService service, CancellationToken ct)
                => (await service.ListDraftsAsync(sessionId, ct)).ToApiResult())
            .WithName("ListModDrafts")
            .Produces<IReadOnlyList<ModDraftView>>();

        drafts.MapGet("/drafts/{name}/files", async (string sessionId, string name, ModService service, CancellationToken ct)
                => (await service.ReadDraftFilesAsync(sessionId, name, ct)).ToApiResult())
            .WithName("GetModDraftFiles")
            .Produces<ModFilesView>();

        // The static check, as the mod host's checker reports it; { "check": null } while there's no checker.
        drafts.MapGet("/drafts/{name}/check", async (string sessionId, string name, ModService service, CancellationToken ct)
                => (await service.CheckDraftAsync(sessionId, name, ct)).ToApiResult())
            .WithName("CheckModDraft")
            .Produces<ModCheckView>();

        // POST /api/sessions/{sessionId}/mods/drafts/{name}/keep  { "note": "show failing names" } — the body is optional.
        drafts.MapPost("/drafts/{name}/keep", async (string sessionId, string name, HttpContext http, ModService service, CancellationToken ct) =>
        {
            KeepModRequest? request;
            try
            {
                request = http.Request.ContentLength is null or 0
                    ? null
                    : await JsonSerializer.DeserializeAsync(http.Request.Body, ApiJsonContext.Default.KeepModRequest, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new ApiErrorResponse("The request body isn't valid."));
            }

            return (await service.KeepAsync(sessionId, name, request?.Note, check: null, ct)).ToApiResult();
        })
            .WithName("KeepModDraft")
            .Produces<ModView>();

        drafts.MapPost("/drafts/{name}/off", async (string sessionId, string name, ModService service, CancellationToken ct)
                => (await service.SetDraftOnAsync(sessionId, name, on: false, ct)).ToApiResult())
            .WithName("TurnModDraftOff")
            .Produces<ModDraftView>();

        drafts.MapPost("/drafts/{name}/on", async (string sessionId, string name, ModService service, CancellationToken ct)
                => (await service.SetDraftOnAsync(sessionId, name, on: true, ct)).ToApiResult())
            .WithName("TurnModDraftOn")
            .Produces<ModDraftView>();

        return app;
    }

    /// <summary>Answers 404 with the switch's message unless Mods are turned on in Settings (safe mode doesn't matter here).</summary>
    private static async ValueTask<object?> OnlyWhenSwitchedOnAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => await context.HttpContext.RequestServices.GetRequiredService<ModsFeature>().IsSwitchedOnAsync()
            ? await next(context)
            : Results.NotFound(new ApiErrorResponse(ModsFeature.TurnedOffMessage));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UseModVersionRequest(int Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetModsSafeModeRequest(bool On);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KeepModRequest(string? Note = null);

#pragma warning restore IL2026
