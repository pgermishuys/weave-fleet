using WeaveFleet.Application.Memory.Dreaming;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>Dreaming: Fleet reads a repository's recent sessions and changes its memory notes. Every route is a 404 while it's off.</summary>
public static class DreamEndpoints
{
    public static IEndpointRouteBuilder MapDreamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory/dreams").WithTags("Memory");

        // GET /api/memory/dreams?repository={any folder in it}: the repository's dreams, newest first.
        group.MapGet("/", async (string? repository, DreamService dreams, CancellationToken ct)
                => !await dreams.IsEnabledAsync() ? Off() : Results.Ok(new DreamListView(await dreams.ListAsync(repository, ct))))
            .WithName("ListDreams")
            .Produces<DreamListView>();

        // POST /api/memory/dreams  { "repository": "…", "harnessType"?, "providerId"?, "modelId"? }
        group.MapPost("/", async (HttpContext http, StartDreamRequest req, DreamService dreams, CancellationToken ct) =>
            {
                if (http.RefuseAgent("when Fleet dreams") is { } refused)
                    return refused;
                return !await dreams.IsEnabledAsync() ? Off() : (await dreams.StartAsync(req, ct)).ToApiResult();
            })
            .WithName("StartDream")
            .Produces<DreamView>();

        group.MapPost("/{dreamId}/changes/{changeId}/undo", async (HttpContext http, string dreamId, string changeId, DreamService dreams, CancellationToken ct) =>
            {
                if (http.RefuseAgent("what dreaming changed") is { } refused)
                    return refused;
                return !await dreams.IsEnabledAsync() ? Off() : (await dreams.UndoAsync(dreamId, changeId, ct)).ToApiResult();
            })
            .WithName("UndoDreamChange")
            .Produces<DreamView>();

        return app;
    }

    private static IResult Off() => Results.NotFound(new ApiErrorResponse(DreamRules.TurnedOffMessage));
}

#pragma warning restore IL2026
