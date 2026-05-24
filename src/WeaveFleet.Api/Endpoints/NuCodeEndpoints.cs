using NuCode.Providers;
using NuCode.Providers.Auth;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class NuCodeEndpoints
{
    public static IEndpointRouteBuilder MapNuCodeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/nucode").WithTags("NuCode");

        // ── Connection test ────────────────────────────────────────────────────
        group.MapPost("/test-connection", async (
            INuCodeConnectionTester tester,
            CancellationToken ct) =>
        {
            var result = await tester.TestAsync(ct).ConfigureAwait(false);
            return Results.Ok(new NuCodeTestConnectionResponse(result.Success, result.Error, result.LatencyMs));
        })
        .Produces<NuCodeTestConnectionResponse>(StatusCodes.Status200OK)
        .WithName("TestNuCodeConnection");

        // ── Provider listing ───────────────────────────────────────────────────
        group.MapGet("/providers", async (
            IProviderRegistry registry,
            INuCodeCredentialStore credentialStore,
            CancellationToken ct) =>
        {
            var configuredIds = await credentialStore.ListConfiguredProviderIdsAsync(ct).ConfigureAwait(false);
            var configuredSet = new HashSet<string>(configuredIds, StringComparer.OrdinalIgnoreCase);

            var providers = registry.GetAll()
                .Select(p => new NuCodeProviderResponse(
                    Id: p.Id,
                    DisplayName: p.DisplayName,
                    Description: p.Description,
                    AuthMechanism: p.AuthMechanism.ToString(),
                    IsConnected: configuredSet.Contains(p.Id),
                    CredentialOptional: p.CredentialOptional,
                    SupportsCustomBaseUrl: p.SupportsCustomBaseUrl,
                    CredentialFields: p.CredentialFields
                        .Select(f => new NuCodeCredentialFieldResponse(
                            f.Key, f.DisplayName, f.Required, f.IsSecret, f.HelpText))
                        .ToList()))
                .ToList();

            return Results.Ok(providers);
        })
        .Produces<List<NuCodeProviderResponse>>(StatusCodes.Status200OK)
        .WithName("ListNuCodeProviders");

        // ── Provider detail ────────────────────────────────────────────────────
        group.MapGet("/providers/{id}", async (
            string id,
            IProviderRegistry registry,
            INuCodeCredentialStore credentialStore,
            CancellationToken ct) =>
        {
            var provider = registry.GetById(id);
            if (provider is null)
                return Results.NotFound();

            var storedCreds = await credentialStore.GetAllForProviderAsync(id, ct).ConfigureAwait(false);
            var isConnected = storedCreds.Count > 0;

            var response = new NuCodeProviderDetailResponse(
                Id: provider.Id,
                DisplayName: provider.DisplayName,
                Description: provider.Description,
                AuthMechanism: provider.AuthMechanism.ToString(),
                IsConnected: isConnected,
                CredentialOptional: provider.CredentialOptional,
                SupportsCustomBaseUrl: provider.SupportsCustomBaseUrl,
                CredentialFields: provider.CredentialFields
                    .Select(f => new NuCodeCredentialFieldResponse(
                        f.Key, f.DisplayName, f.Required, f.IsSecret, f.HelpText))
                    .ToList(),
                StoredFields: storedCreds
                    .Select(c => new NuCodeStoredFieldResponse(c.FieldKey, c.DisplayHint, c.ExpiresAt))
                    .ToList());

            return Results.Ok(response);
        })
        .Produces<NuCodeProviderDetailResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("GetNuCodeProvider");

        // ── Store credentials ──────────────────────────────────────────────────
        group.MapPut("/providers/{id}/credentials", async (
            string id,
            NuCodeStoreCredentialsRequest request,
            IProviderRegistry registry,
            INuCodeCredentialStore credentialStore,
            CancellationToken ct) =>
        {
            var provider = registry.GetById(id);
            if (provider is null)
                return Results.NotFound();

            foreach (var (fieldKey, value) in request.Fields)
            {
                await credentialStore.SetAsync(id, fieldKey, value, ct: ct).ConfigureAwait(false);
            }

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("StoreNuCodeProviderCredentials");

        // ── Disconnect provider ────────────────────────────────────────────────
        group.MapDelete("/providers/{id}/credentials", async (
            string id,
            INuCodeCredentialStore credentialStore,
            CancellationToken ct) =>
        {
            await credentialStore.DeleteAllForProviderAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .WithName("DisconnectNuCodeProvider");

        // ── Test provider connection ───────────────────────────────────────────
        group.MapPost("/providers/{id}/test", async (
            string id,
            IProviderRegistry registry,
            INuCodeConnectionTester tester,
            CancellationToken ct) =>
        {
            var provider = registry.GetById(id);
            if (provider is null)
                return Results.NotFound();

            var result = await tester.TestAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new NuCodeTestConnectionResponse(result.Success, result.Error, result.LatencyMs));
        })
        .Produces<NuCodeTestConnectionResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("TestNuCodeProviderConnection");

        // ── Initiate OAuth device flow ─────────────────────────────────────────
        group.MapPost("/providers/{id}/auth/device-code", async (
            string id,
            IProviderRegistry registry,
            INuCodeHttpClient httpClient,
            CancellationToken ct) =>
        {
            var provider = registry.GetById(id);
            if (provider is null)
                return Results.NotFound();

            if (provider.AuthMechanism != AuthMechanism.OAuthDevice)
            {
                return Results.BadRequest(new { error = $"Provider '{id}' does not use OAuth device flow." });
            }

            var flow = new OAuthDeviceFlow(httpClient);
            try
            {
                var deviceCode = await flow.RequestDeviceCodeAsync(ct).ConfigureAwait(false);
                return Results.Ok(new NuCodeDeviceCodeResponse(
                    deviceCode.DeviceCode,
                    deviceCode.UserCode,
                    deviceCode.VerificationUri,
                    deviceCode.ExpiresIn,
                    deviceCode.Interval));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to initiate device flow: {ex.Message}" });
            }
        })
        .Produces<NuCodeDeviceCodeResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("InitiateNuCodeDeviceFlow");

        // ── Poll OAuth device flow ────────────────────────────────────────────
        group.MapPost("/providers/{id}/auth/poll", async (
            string id,
            NuCodeDevicePollRequest request,
            IProviderRegistry registry,
            INuCodeCredentialStore credentialStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var provider = registry.GetById(id);
            if (provider is null)
                return Results.NotFound();

            if (provider.AuthMechanism != AuthMechanism.OAuthDevice)
            {
                return Results.BadRequest(new { error = $"Provider '{id}' does not use OAuth device flow." });
            }

            // Poll GitHub directly — matching the proven GitHubService pattern
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            var pollBody = new FormUrlEncodedContent([
                new("client_id", "Ov23liJT2Q0HXHj9xLGM"),
                new("device_code", request.DeviceCode),
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            ]);

            var response = await client.PostAsync(
                "https://github.com/login/oauth/access_token",
                pollBody,
                ct).ConfigureAwait(false);

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var json = System.Text.Json.Nodes.JsonNode.Parse(responseBody) as System.Text.Json.Nodes.JsonObject;

            if (json is null)
                return Results.Ok(new NuCodeDevicePollResponse("error", null, "GitHub returned an empty response."));

            if (json.TryGetPropertyValue("access_token", out var tokenNode) && tokenNode is System.Text.Json.Nodes.JsonValue)
            {
                var token = tokenNode.GetValue<string>();
                await credentialStore.SetAsync(provider.Id, "githubToken", token, ct: ct).ConfigureAwait(false);
                return Results.Ok(new NuCodeDevicePollResponse("complete", null, null));
            }

            var error = json["error"]?.GetValue<string>();
            var interval = json["interval"]?.GetValue<int?>();

            return error switch
            {
                "authorization_pending" => Results.Ok(new NuCodeDevicePollResponse("pending", interval, null)),
                "slow_down" => Results.Ok(new NuCodeDevicePollResponse("pending", interval, null)),
                "expired_token" => Results.Ok(new NuCodeDevicePollResponse("expired", null, "The code expired. Please try again.")),
                "access_denied" => Results.Ok(new NuCodeDevicePollResponse("denied", null, "Authorization was denied.")),
                _ => Results.Ok(new NuCodeDevicePollResponse("error", null, json["error_description"]?.GetValue<string>() ?? error ?? "Unknown error")),
            };
        })
        .Produces<NuCodeDevicePollResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("PollNuCodeDeviceFlow");

        // ── Provider config (baseUrl, resourceName, etc.) ─────────────────────
        group.MapPut("/providers/{id}/config", async (
            string id,
            NuCodeProviderConfigRequest request,
            IProviderRegistry registry,
            INuCodeCredentialStore credentialStore,
            CancellationToken ct) =>
        {
            var provider = registry.GetById(id);
            if (provider is null)
                return Results.NotFound();

            // Store non-secret config fields alongside credentials using a "config:" prefix on fieldKey
            foreach (var (key, value) in request.Options)
            {
                await credentialStore.SetAsync(id, $"config:{key}", value, ct: ct).ConfigureAwait(false);
            }

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .WithName("ConfigureNuCodeProvider");

        return app;
    }
}

// ── Response / Request types ───────────────────────────────────────────────────

internal sealed record NuCodeTestConnectionResponse(bool Success, string? Error, int LatencyMs);

internal sealed record NuCodeProviderResponse(
    string Id,
    string DisplayName,
    string? Description,
    string AuthMechanism,
    bool IsConnected,
    bool CredentialOptional,
    bool SupportsCustomBaseUrl,
    List<NuCodeCredentialFieldResponse> CredentialFields);

internal sealed record NuCodeProviderDetailResponse(
    string Id,
    string DisplayName,
    string? Description,
    string AuthMechanism,
    bool IsConnected,
    bool CredentialOptional,
    bool SupportsCustomBaseUrl,
    List<NuCodeCredentialFieldResponse> CredentialFields,
    List<NuCodeStoredFieldResponse> StoredFields);

internal sealed record NuCodeCredentialFieldResponse(
    string Key,
    string DisplayName,
    bool Required,
    bool IsSecret,
    string? HelpText);

internal sealed record NuCodeStoredFieldResponse(
    string FieldKey,
    string? DisplayHint,
    DateTimeOffset? ExpiresAt);

internal sealed record NuCodeStoreCredentialsRequest(
    Dictionary<string, string> Fields);

internal sealed record NuCodeProviderConfigRequest(
    Dictionary<string, string> Options);

internal sealed record NuCodeDeviceFlowInitiatedResponse(string Instructions);

internal sealed record NuCodeDeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);

internal sealed record NuCodeDevicePollRequest(string DeviceCode);

internal sealed record NuCodeDevicePollResponse(string Status, int? Interval, string? Message);

#pragma warning restore IL2026
