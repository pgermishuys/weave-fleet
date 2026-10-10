#if FLEET_MODS_DEBUG
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// The mod host as tests see it: its status, a mod's log, and a dispatch. Only compiled with <c>-p:FleetModsDebug=true</c>,
/// so no Fleet that ships has these routes; M6 and M8 give users their own views.
/// </summary>
public static class ModHostDebugEndpoints
{
    public static IEndpointRouteBuilder MapModHostDebugEndpoints(this IEndpointRouteBuilder app)
    {
        var debug = app.MapGroup("/api/mods/debug").WithTags("Mods");

        debug.MapGet("/host", (IModHost host, IUserContext user)
            => Results.Json(host.GetStatus(user.UserId), ModHostDebugJsonContext.Default.ModHostStatus));

        debug.MapGet("/log/{modId}", (string modId, IModHost host, IUserContext user)
            => Results.Json(new ModHostDebugLog(modId, host.GetLog(user.UserId, modId), host.GetLoadProblem(user.UserId, modId)), ModHostDebugJsonContext.Default.ModHostDebugLog));

        // The body is read with this file's own context, so the debug types never join the app's serializer options.
        debug.MapPost("/dispatch", async (HttpRequest http, IModHost host, IUserContext user, CancellationToken ct) =>
        {
            var request = await JsonSerializer.DeserializeAsync(http.Body, ModHostDebugJsonContext.Default.ModHostDebugDispatch, ct);
            if (request is null)
                return Results.BadRequest();
            var result = await host.DispatchAsync(user.UserId, new ModDispatchRequest(request.Event, request.SessionId, request.E, request.Surface), ct);
            return Results.Json(result, ModHostDebugJsonContext.Default.ModDispatchResult);
        });

        return app;
    }
}

internal sealed record ModHostDebugDispatch(string Event, string SessionId, JsonElement E, string? Surface = null);

internal sealed record ModHostDebugLog(string Mod, IReadOnlyList<ModLogLine> Lines, ModLoadProblem? Problem);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModHostStatus))]
[JsonSerializable(typeof(ModHostDebugDispatch))]
[JsonSerializable(typeof(ModHostDebugLog))]
[JsonSerializable(typeof(ModDispatchResult))]
internal sealed partial class ModHostDebugJsonContext : JsonSerializerContext;

#pragma warning restore IL2026
#endif
