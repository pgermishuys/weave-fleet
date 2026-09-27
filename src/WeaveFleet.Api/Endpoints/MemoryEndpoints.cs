using System.Text.Json.Serialization;
using WeaveFleet.Application.Memory;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>Settings → Memory: the switch, and the notes agents keep about each repository and this machine.</summary>
public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory").WithTags("Memory");

        group.MapGet("/", async (AgentMemoryService memory, CancellationToken ct) => Results.Ok(await memory.GetOverviewAsync(ct)))
            .WithName("GetMemory")
            .Produces<MemoryOverview>();

        // PUT /api/memory  { "enabled": true }. Off keeps the notes; sessions stop reading them.
        group.MapPut("/", async (SetMemoryEnabledRequest req, AgentMemoryService memory, CancellationToken ct)
                => Results.Ok(await memory.SetEnabledAsync(req.Enabled, ct)))
            .WithName("SetMemoryEnabled")
            .Produces<MemoryOverview>();

        // GET /api/memory/notes?repository={any folder in it}: that repository's notes and the machine's.
        group.MapGet("/notes", async (string? repository, AgentMemoryService memory, CancellationToken ct)
                => Results.Ok(await memory.ListAsync(repository, ct)))
            .WithName("ListMemoryNotes")
            .Produces<MemoryNotesView>();

        group.MapPost("/notes", async (AddMemoryNoteRequest req, AgentMemoryService memory, CancellationToken ct)
                => (await memory.AddAsync(req.List, req.Repository, req.Text, ct)).ToApiResult())
            .WithName("AddMemoryNote")
            .Produces<MemoryNoteView>();

        group.MapPut("/notes/{id}", async (string id, UpdateMemoryNoteRequest req, AgentMemoryService memory, CancellationToken ct)
                => (await memory.UpdateAsync(id, req.Text, ct)).ToApiResult())
            .WithName("UpdateMemoryNote")
            .Produces<MemoryNoteView>();

        group.MapDelete("/notes/{id}", async (string id, AgentMemoryService memory, CancellationToken ct)
                => (await memory.ForgetAsync(id, ct)).ToApiResult())
            .WithName("ForgetMemoryNote");

        // POST /api/memory/clear  { "scope": "all" | "machine" | "repository", "repository": "…" }
        group.MapPost("/clear", async (ClearMemoryRequest req, AgentMemoryService memory, CancellationToken ct) =>
            {
                var cleared = await memory.ClearAsync(req.Scope, req.Repository, ct);
                return cleared.IsSuccess ? Results.Ok(new ClearMemoryResponse(cleared.Value)) : cleared.ToApiResult();
            })
            .WithName("ClearMemory")
            .Produces<ClearMemoryResponse>();

        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetMemoryEnabledRequest(bool Enabled);

internal sealed record AddMemoryNoteRequest(string? List, string? Repository, string? Text);

internal sealed record UpdateMemoryNoteRequest(string? Text);

internal sealed record ClearMemoryRequest(string? Scope, string? Repository);

internal sealed record ClearMemoryResponse(int Deleted);

#pragma warning restore IL2026
