using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

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
            var onboardingStatus = await userService.GetOnboardingStatusAsync(user);

            return Results.Ok(new UserMeResponse(
                userContext.UserId,
                userContext.Email,
                userContext.DisplayName,
                onboardingStatus.Completed,
                onboardingStatus,
                user?.CreatedAt ?? string.Empty));
        })
        .Produces<UserMeResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .WithName("GetCurrentUser");

        group.MapPost("/me/complete-onboarding", async (
            IUserContext userContext,
            UserService userService,
            FleetOptions fleetOptions) =>
        {
            if (fleetOptions.Auth.Enabled && !userContext.IsAuthenticated)
                return Results.Unauthorized();

            if (fleetOptions.Auth.Enabled)
            {
                await userService.EnsureUserAsync(userContext);
                await userService.CompleteOnboardingAsync(userContext);
            }

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .WithName("CompleteOnboarding");

        return app;
    }
}

internal sealed record UserMeResponse(
    string UserId,
    string? Email,
    string? DisplayName,
    bool OnboardingCompleted,
    UserOnboardingStatus OnboardingStatus,
    string CreatedAt);
#pragma warning restore IL2026
