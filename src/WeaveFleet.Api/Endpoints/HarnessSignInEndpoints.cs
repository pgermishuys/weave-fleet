using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Sign in to a harness's providers from Settings (<see cref="HarnessSignInService"/>). Only with Fleet's own sign-in
/// off: a harness's sign-ins are the machine's, shared by every user's sessions. Keys and codes travel in request
/// bodies, never in addresses, so nothing that logs a request's path sees them.
/// </summary>
public static class HarnessSignInEndpoints
{
    public static IEndpointRouteBuilder MapHarnessSignInEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harnesses/{harnessType}/sign-in").WithTags("Harness sign-in");

        // GET /api/harnesses/{harnessType}/sign-in — every provider, how to sign in to it, and its sign-ins
        group.MapGet("/", async (string harnessType, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.ListAsync(harnessType, ct)).ToApiResult())
            .Produces<HarnessSignIns>()
            .WithName("ListHarnessSignIns");

        // POST /api/harnesses/{harnessType}/sign-in/{providerId}/key  { "key": "…", "answers": { … } }
        group.MapPost("/{providerId}/key", async (string harnessType, string providerId, SignInWithKeyRequest req, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.SignInWithKeyAsync(harnessType, providerId, req.Key, req.Answers, ct)).ToNoContentResult())
            .WithName("SignInWithKey");

        // POST /api/harnesses/{harnessType}/sign-in/{providerId}/attempts  { "methodId": "…", "answers": { … } } — a browser sign-in
        group.MapPost("/{providerId}/attempts", async (string harnessType, string providerId, StartSignInRequest req, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.StartAsync(harnessType, providerId, req.MethodId, req.Answers, ct)).ToApiResult())
            .Produces<HarnessSignInAttempt>()
            .WithName("StartHarnessSignIn");

        group.MapGet("/{providerId}/attempts/{attemptId}", async (string harnessType, string providerId, string attemptId, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.GetAttemptAsync(harnessType, providerId, attemptId, ct)).ToApiResult())
            .Produces<HarnessSignInAttemptStatus>()
            .WithName("GetHarnessSignIn");

        // POST …/attempts/{attemptId}/code  { "code": "…" } — for a sign-in that shows a code to paste back
        group.MapPost("/{providerId}/attempts/{attemptId}/code", async (string harnessType, string providerId, string attemptId, SignInCodeRequest req, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.SubmitCodeAsync(harnessType, providerId, attemptId, req.Code, ct)).ToNoContentResult())
            .WithName("SubmitHarnessSignInCode");

        // POST …/attempts/{attemptId}/callback  { "address": "http://localhost:…?code=…" } — from a browser on another
        // device, which couldn't open the localhost page the provider sent it to
        group.MapPost("/{providerId}/attempts/{attemptId}/callback", async (string harnessType, string providerId, string attemptId, SignInCallbackRequest req, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.ForwardCallbackAsync(harnessType, providerId, attemptId, req.Address, ct)).ToNoContentResult())
            .WithName("ForwardHarnessSignInCallback");

        group.MapDelete("/{providerId}/attempts/{attemptId}", async (string harnessType, string providerId, string attemptId, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.CancelAsync(harnessType, providerId, attemptId, ct)).ToNoContentResult())
            .WithName("CancelHarnessSignIn");

        // POST /api/harnesses/{harnessType}/sign-in/connections/{connectionId}/use — make it the one its provider uses
        group.MapPost("/connections/{connectionId}/use", async (string harnessType, string connectionId, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.UseAsync(harnessType, connectionId, ct)).ToNoContentResult())
            .WithName("UseHarnessSignIn");

        // DELETE /api/harnesses/{harnessType}/sign-in/connections/{connectionId} — sign out
        group.MapDelete("/connections/{connectionId}", async (string harnessType, string connectionId, HarnessSignInService signIn, CancellationToken ct) =>
            (await signIn.SignOutAsync(harnessType, connectionId, ct)).ToNoContentResult())
            .WithName("SignOutOfHarnessProvider");

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SignInWithKeyRequest(string? Key, Dictionary<string, JsonElement>? Answers);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StartSignInRequest(string? MethodId, Dictionary<string, JsonElement>? Answers);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SignInCodeRequest(string? Code);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SignInCallbackRequest(string? Address);

#pragma warning restore IL2026
