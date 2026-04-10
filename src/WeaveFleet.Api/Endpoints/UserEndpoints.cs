using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/user").WithTags("User");

        group.MapGet("/me", async (
            IUserContext userContext,
            UserService userService,
            FleetOptions fleetOptions) =>
        {
            if (fleetOptions.Auth.Enabled && !userContext.IsAuthenticated)
                return Results.Unauthorized();

            var user = fleetOptions.Auth.Enabled
                ? await userService.EnsureUserAsync(userContext)
                : null;

            return Results.Ok(new UserMeResponse(
                userContext.UserId,
                userContext.Email,
                userContext.DisplayName,
                user?.OnboardingCompletedAt is not null,
                user?.CreatedAt ?? string.Empty));
        })
        .Produces<UserMeResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .WithName("GetCurrentUser");

        return app;
    }
}

internal sealed record UserMeResponse(
    string UserId,
    string? Email,
    string? DisplayName,
    bool OnboardingCompleted,
    string CreatedAt);
