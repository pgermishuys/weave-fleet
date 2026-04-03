using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// GitHub OAuth device flow endpoints.
/// </summary>
public static class GitHubAuthEndpoints
{
    public static WebApplication MapGitHubAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/github/auth").WithTags("GitHub");

        // POST /api/integrations/github/auth/device-code — initiates GitHub OAuth device flow
        group.MapPost("/device-code", async (GitHubService gitHubService, CancellationToken ct) =>
        {
            var response = await gitHubService.InitiateDeviceFlowAsync(ct);
            if (response is null)
                return Results.Problem("Failed to initiate GitHub device flow.");

            return Results.Ok(new
            {
                deviceCode = response.DeviceCode,
                userCode = response.UserCode,
                verificationUri = response.VerificationUri,
                expiresIn = response.ExpiresIn,
                interval = response.Interval
            });
        })
        .WithName("GitHubInitiateDeviceFlow");

        // POST /api/integrations/github/auth/poll — polls GitHub for access token
        group.MapPost("/poll", async (
            PollRequest req,
            GitHubService gitHubService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.DeviceCode))
                return Results.BadRequest(new { error = "deviceCode is required." });

            var token = await gitHubService.PollForTokenAsync(req.DeviceCode, ct);

            if (token is null)
                return Results.Ok(new { status = "pending" });

            return Results.Ok(new { status = "success" });
        })
        .WithName("GitHubPollForToken");

        // DELETE /api/integrations/github/auth — disconnect (remove stored token)
        group.MapDelete("/", async (GitHubService gitHubService, CancellationToken ct) =>
        {
            await gitHubService.DisconnectAsync(ct);
            return Results.NoContent();
        })
        .WithName("GitHubDisconnect");

        // GET /api/integrations/github/auth/status — check connection status
        group.MapGet("/status", async (GitHubService gitHubService, CancellationToken ct) =>
        {
            var connected = await gitHubService.IsConnectedAsync(ct);
            return Results.Ok(new { connected });
        })
        .WithName("GitHubConnectionStatus");

        return app;
    }

    private sealed record PollRequest(string DeviceCode);
}
