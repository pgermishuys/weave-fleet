using WeaveFleet.Api;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// API endpoints for managing user credentials.
/// Values are NEVER included in any response — only metadata (label, provider, type, displayHint).
/// Terminology is capability-level: "API keys", "credentials" — not "environment variables".
/// </summary>
public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/credentials").WithTags("Credentials");

        // GET /api/credentials — list stored credentials with metadata (no values)
        group.MapGet("/", async (ICredentialStore credentialStore) =>
        {
            var credentials = await credentialStore.ListCredentialsAsync();
            return Results.Ok(credentials.Select(c => new CredentialResponse(
                c.Id,
                c.Label,
                c.Namespace,
                c.Kind,
                c.DisplayHint,
                c.Metadata,
                c.CreatedAt,
                c.UpdatedAt)).ToList());
        })
        .Produces<IReadOnlyList<CredentialResponse>>(StatusCodes.Status200OK)
        .WithName("ListCredentials");

        // PUT /api/credentials — store a new credential (create or update by label)
        group.MapPut("/", async (
            StoreCredentialRequest request,
            ICredentialStore credentialStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.Label))
                return Results.BadRequest(new ErrorResponse("Label is required."));
            if (string.IsNullOrWhiteSpace(request.Namespace))
                return Results.BadRequest(new ErrorResponse("Provider is required."));
            if (string.IsNullOrWhiteSpace(request.Kind))
                return Results.BadRequest(new ErrorResponse("Type is required."));
            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new ErrorResponse("API key value is required."));

            await credentialStore.StoreCredentialAsync(
                request.Label,
                request.Namespace,
                request.Kind,
                request.Value,
                request.Metadata);

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .WithName("StoreCredential");

        // PUT /api/credentials/{id} — update an existing credential value/metadata
        group.MapPut("/{id}", async (
            string id,
            UpdateCredentialRequest request,
            ICredentialStore credentialStore) =>
        {
            // Fetch existing credential to get its current label/namespace/kind, then re-store with new value
            var credentials = await credentialStore.ListCredentialsAsync();
            var existing = credentials.FirstOrDefault(c => c.Id == id);

            if (existing is null)
                return Results.NotFound(new ErrorResponse($"Credential '{id}' not found."));

            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new ErrorResponse("API key value is required."));

            await credentialStore.StoreCredentialAsync(
                existing.Label,
                existing.Namespace,
                existing.Kind,
                request.Value,
                request.Metadata ?? existing.Metadata);

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("UpdateCredential");

        // DELETE /api/credentials/{id} — remove a stored credential
        group.MapDelete("/{id}", async (string id, ICredentialStore credentialStore) =>
        {
            await credentialStore.DeleteCredentialAsync(id);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .WithName("DeleteCredential");

        return app;
    }
}

/// <summary>Metadata-only response for a stored credential. Values never included.</summary>
internal sealed record CredentialResponse(
    string Id,
    string Label,
    string Namespace,
    string Kind,
    string DisplayHint,
    string? Metadata,
    string CreatedAt,
    string UpdatedAt);

/// <summary>Request to store a new API key credential.</summary>
internal sealed record StoreCredentialRequest(
    string Label,
    string Namespace,
    string Kind,
    string Value,
    string? Metadata);

/// <summary>Request to update an existing credential's value.</summary>
internal sealed record UpdateCredentialRequest(
    string Value,
    string? Metadata);
#pragma warning restore IL2026
