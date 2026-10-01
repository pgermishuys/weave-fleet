using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Api.Endpoints;

/// <summary>SPIKE (branch spike/opencode2-browser-plugin): attach Fleet's headless Chrome to a session's OpenCode 2 browser plugin.</summary>
public static class BrowserSpikeEndpoints
{
    public static IEndpointRouteBuilder MapBrowserSpikeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/spike/sessions/{id}/browser-attach", async (string id, OpenCode2BrowserSpike spike, IUserContext user, CancellationToken ct)
            => Results.Text(await spike.AttachAsync(user.UserId, id, ct)));
        app.MapGet("/api/spike/sessions/{id}/browser-log", (string id, OpenCode2BrowserSpike spike)
            => Results.Text(string.Join('\n', spike.Log(id))));
        return app;
    }
}
