using System.Text.Json.Serialization;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>A harness's profiles: config the user keeps in Fleet and picks when starting a session.</summary>
public static class HarnessProfileEndpoints
{
    public static IEndpointRouteBuilder MapHarnessProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harnesses/{harnessType}/profiles").WithTags("Harness profiles");

        group.MapGet("/", async (string harnessType, HarnessProfileService profiles) =>
            (await profiles.ListAsync(harnessType)).ToApiResult())
            .WithName("ListHarnessProfiles");

        // Saving checks the profile with the harness first; a profile it can't load is refused with its error.
        group.MapPost("/", async (string harnessType, SaveHarnessProfileRequest req, HarnessProfileService profiles, CancellationToken ct) =>
            (await profiles.CreateAsync(harnessType, req.Name, req.Content, ct)).ToApiResult())
            .WithName("CreateHarnessProfile");

        group.MapPut("/{id}", async (string harnessType, string id, SaveHarnessProfileRequest req, HarnessProfileService profiles, CancellationToken ct) =>
            (await profiles.UpdateAsync(harnessType, id, req.Name, req.Content, ct)).ToApiResult())
            .WithName("UpdateHarnessProfile");

        group.MapDelete("/{id}", async (string harnessType, string id, HarnessProfileService profiles) =>
            (await profiles.DeleteAsync(harnessType, id)).ToNoContentResult())
            .WithName("DeleteHarnessProfile");

        // PUT /api/harnesses/{harnessType}/profiles/default  { "profileId": "…" }  (null: new sessions use no profile)
        group.MapPut("/default", async (string harnessType, SetDefaultHarnessProfileRequest req, HarnessProfileService profiles) =>
            (await profiles.SetDefaultAsync(harnessType, req.ProfileId)).ToNoContentResult())
            .WithName("SetDefaultHarnessProfile");

        // Tries content without saving it.
        group.MapPost("/check", async (string harnessType, CheckHarnessProfileRequest req, HarnessProfileService profiles, CancellationToken ct) =>
            (await profiles.CheckAsync(harnessType, req.Content, ct)).ToApiResult())
            .WithName("CheckHarnessProfile");

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SaveHarnessProfileRequest(string? Name, string? Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetDefaultHarnessProfileRequest(string? ProfileId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CheckHarnessProfileRequest(string? Content);

#pragma warning restore IL2026
