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

        // ── The user's own versions ──────────────────────────────────────────
        // A version replaces Fleet's copy of the skill in the sessions started afterwards. Fleet never overwrites it.

        group.MapGet("/{name}", async (string name, BuiltInSkillService skills) => (await skills.GetAsync(name)).ToApiResult())
            .WithName("GetBuiltInSkill")
            .Produces<BuiltInSkillDetail>();

        // POST /api/skills/built-in/{name}/versions  { "content": "---\nname: …", "note": "why", "sessionId": "…" }
        group.MapPost("/{name}/versions", async (string name, SaveSkillVersionRequest req, BuiltInSkillService skills) =>
            (await skills.SaveVersionAsync(name, req.Content, req.Note, req.SessionId)).ToApiResult())
            .WithName("SaveBuiltInSkillVersion")
            .Produces<BuiltInSkillDetail>();

        group.MapGet("/{name}/versions/{version:int}", async (string name, int version, BuiltInSkillService skills) =>
            (await skills.ReadVersionAsync(name, version)).ToApiResult())
            .WithName("GetBuiltInSkillVersion")
            .Produces<SkillVersionContent>();

        // PUT /api/skills/built-in/{name}/active  { "version": 2 } — or null for Fleet's.
        group.MapPut("/{name}/active", async (string name, UseSkillVersionRequest req, BuiltInSkillService skills) =>
            (await skills.UseVersionAsync(name, req.Version)).ToApiResult())
            .WithName("UseBuiltInSkillVersion")
            .Produces<BuiltInSkillDetail>();

        // After Fleet changed its version: keep the user's, which puts the notice away.
        group.MapPost("/{name}/keep-mine", async (string name, BuiltInSkillService skills) =>
            (await skills.KeepMineAsync(name)).ToApiResult())
            .WithName("KeepBuiltInSkillVersion")
            .Produces<BuiltInSkillDetail>();

        // Asks the model off the record for a better version. Nothing is saved until POST /versions.
        group.MapPost("/{name}/improve", async (string name, ImproveSkillRequest req, SkillImprover improver, CancellationToken ct) =>
            (await improver.ImproveAsync(name, req, ct)).ToApiResult())
            .WithName("ImproveBuiltInSkill")
            .Produces<SkillProposal>();

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetBuiltInSkillRequest(bool Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SaveSkillVersionRequest(string Content, string? Note = null, string? SessionId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UseSkillVersionRequest(int? Version);

#pragma warning restore IL2026
