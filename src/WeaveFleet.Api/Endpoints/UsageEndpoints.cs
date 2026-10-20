using WeaveFleet.Application.Usage;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/usage/limits").WithTags("Usage");

        // GET /api/usage/limits?watch= — each provider's usage limits; watch=true when the popover is open. Off: {enabled:false}.
        group.MapGet("/", async (bool? watch, UsageLimitsService usage, IUserContext user, CancellationToken ct)
            => Results.Ok(await usage.GetAsync(user.UserId, watch == true, ct)))
        .WithName("GetUsageLimits")
        .Produces<UsageLimitsSnapshot>();

        // POST/DELETE /api/usage/limits/{provider}/allow — the user lets Fleet ask a provider with the sign-in it found, or takes that back.
        group.MapPost("/{provider}/allow", async (string provider, UsageLimitsService usage, IUserContext user, HttpContext http, CancellationToken ct) =>
        {
            if (http.RefuseAgent("which providers Fleet asks for usage limits") is { } refused)
                return refused;
            return await usage.SetAllowedAsync(user.UserId, provider, true, ct) ? Results.NoContent() : Results.NotFound(new ApiErrorResponse("Fleet doesn't know that provider."));
        })
        .WithName("AllowUsageLimits");

        group.MapDelete("/{provider}/allow", async (string provider, UsageLimitsService usage, IUserContext user, HttpContext http, CancellationToken ct) =>
        {
            if (http.RefuseAgent("which providers Fleet asks for usage limits") is { } refused)
                return refused;
            return await usage.SetAllowedAsync(user.UserId, provider, false, ct) ? Results.NoContent() : Results.NotFound(new ApiErrorResponse("Fleet doesn't know that provider."));
        })
        .WithName("RevokeUsageLimits");

        return app;
    }
}

#pragma warning restore IL2026
