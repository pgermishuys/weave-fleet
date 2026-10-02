using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// The agent's own browser: a session's tabs and steps, a picture of a tab for Agent's view, and Settings → Browser.
/// Live steps arrive on <c>session:{id}</c> as <c>browser.step</c>.
/// </summary>
public static class AgentBrowserEndpoints
{
    public static IEndpointRouteBuilder MapAgentBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapGroup("/api/sessions").WithTags("Agent browser");

        // GET /api/sessions/{id}/agent-browser — the agent's open tabs and the steps it took
        sessions.MapGet("/{id}/agent-browser", async (string id, SessionService sessionService, IAgentBrowser browser, IAgentBrowserSteps steps, CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var (tabs, focused) = browser.TabsOf(id);
            var list = await steps.ListAsync(id, ct: ct);
            return Results.Ok(new AgentBrowserResponse(tabs, focused, [.. list.Select(AgentBrowserStepStore.ToWire)]));
        })
        .Produces<AgentBrowserResponse>(200)
        .Produces(404)
        .WithName("GetAgentBrowser");

        // GET /api/sessions/{id}/agent-browser/tabs/{tabId}/frame — what the agent's tab shows now, as a JPEG
        sessions.MapGet("/{id}/agent-browser/tabs/{tabId}/frame", async (string id, string tabId, SessionService sessionService, IAgentBrowser browser, HttpContext context, CancellationToken ct) =>
        {
            var session = await sessionService.GetSessionAsync(id);
            if (session.IsFailure)
                return session.Error.ToSessionApiResult();

            var frame = await browser.FrameAsync(id, tabId, ct);
            if (frame is null)
                return Results.NotFound(new ErrorResponse("The agent's tab is closed."));
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(frame, "image/jpeg");
        })
        .Produces(200, contentType: "image/jpeg")
        .Produces(404)
        .WithName("GetAgentBrowserFrame");

        var settings = app.MapGroup("/api/agent-browser").WithTags("Agent browser");

        // GET /api/agent-browser/settings — Settings → Browser
        settings.MapGet("/settings", async (AgentBrowserAccess access) =>
        {
            var current = await access.SettingsAsync();
            return Results.Ok(new AgentBrowserSettingsResponse(current.Enabled, current.Pages, current.Scripts));
        })
        .Produces<AgentBrowserSettingsResponse>(200)
        .WithName("GetAgentBrowserSettings");

        // PUT /api/agent-browser/settings — change some of them; the rest stay
        settings.MapPut("/settings", async (AgentBrowserSettingsRequest request, AgentBrowserAccess access, AgentBrowserSettingsChanges changes, IUserContext user) =>
        {
            if (request.Pages is not null && !AgentBrowserPages.IsKnown(request.Pages))
                return Results.UnprocessableEntity(new ErrorResponse("\"pages\" must be \"session\", \"machine\" or \"any\"."));

            var saved = await access.SaveAsync(request.Enabled, request.Pages, request.Scripts);
            changes.Notify(user.UserId, saved);
            return Results.Ok(new AgentBrowserSettingsResponse(saved.Enabled, saved.Pages, saved.Scripts));
        })
        .Produces<AgentBrowserSettingsResponse>(200)
        .Produces(422)
        .WithName("SaveAgentBrowserSettings");

        return app;
    }
}
