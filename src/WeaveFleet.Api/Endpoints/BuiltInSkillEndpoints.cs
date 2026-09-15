using System.Text.Json.Serialization;
using WeaveFleet.Application.Skills;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>The skills Fleet ships, which the user turns on for their sessions one by one.</summary>
public static class BuiltInSkillEndpoints
{
    public static IEndpointRouteBuilder MapBuiltInSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills/built-in").WithTags("Skills");

        group.MapGet("/", async (BuiltInSkillService skills) => Results.Ok(await skills.ListAsync()))
            .WithName("ListBuiltInSkills")
            .Produces<IReadOnlyList<BuiltInSkillView>>();

        // PUT /api/skills/built-in/{name}  { "enabled": true }. Sessions started afterwards get the change.
        group.MapPut("/{name}", async (string name, SetBuiltInSkillRequest req, BuiltInSkillService skills) =>
            (await skills.SetEnabledAsync(name, req.Enabled)).ToApiResult())
            .WithName("SetBuiltInSkill")
            .Produces<BuiltInSkillView>();

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetBuiltInSkillRequest(bool Enabled);

#pragma warning restore IL2026
