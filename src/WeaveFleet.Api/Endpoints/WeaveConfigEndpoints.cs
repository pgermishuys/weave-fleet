using System.Text.Json.Serialization;
using WeaveFleet.Application.Weave;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>Settings → Weave: which Weave each harness loads, and the config Fleet keeps for it.</summary>
public static class WeaveConfigEndpoints
{
    public static IEndpointRouteBuilder MapWeaveConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/weave").WithTags("Weave");

        // GET /api/weave?redetect=true asks the harnesses again instead of using what they said last time.
        group.MapGet("/", async (bool? redetect, WeaveConfigService weave, CancellationToken ct) =>
            (await weave.GetAsync(redetect == true, ct)).ToApiResult())
            .WithName("GetWeaveConfig");

        // Saving with source "fleet" has the harnesses try the config first; if one fails, nothing is saved and the
        // response says why (saved: false).
        group.MapPut("/", async (SaveWeaveConfigRequest req, WeaveConfigService weave, CancellationToken ct) =>
            (await weave.SaveAsync(req.Source, req.Files, ct)).ToApiResult())
            .WithName("SaveWeaveConfig");

        // GET /api/weave/own?flavor=weave — the user's own Weave files, to start Fleet's config from.
        group.MapGet("/own", (string? flavor, WeaveConfigService weave) => flavor switch
            {
                "weave" => weave.ReadOwn(WeaveFlavor.Weave).ToApiResult(),
                "legacy" => weave.ReadOwn(WeaveFlavor.Legacy).ToApiResult(),
                _ => Results.BadRequest(new ApiErrorResponse("flavor must be 'weave' or 'legacy'.")),
            })
            .WithName("GetOwnWeaveConfig");

        // Tries draft files without saving them.
        group.MapPost("/check", async (CheckWeaveConfigRequest req, WeaveConfigService weave, CancellationToken ct) =>
            (await weave.CheckAsync(req.Flavor, req.Files, ct)).ToApiResult())
            .WithName("CheckWeaveConfig");

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SaveWeaveConfigRequest(string? Source, Dictionary<string, string>? Files);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CheckWeaveConfigRequest(WeaveFlavor Flavor, Dictionary<string, string>? Files);

#pragma warning restore IL2026
