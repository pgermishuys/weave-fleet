using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// GitHub OAuth device flow + token management.
/// </summary>
public sealed class GitHubService(
    IHttpClientFactory httpClientFactory,
    IIntegrationStore integrationStore)
{
    private const string IntegrationId = "github";
    private const string ClientId = "Ov23liJT2Q0HXHj9xLGM";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string Scopes = "repo,read:user,read:org";

    // ── Device Flow ────────────────────────────────────────────────────────────

    /// <summary>Initiates GitHub OAuth device flow. Returns device code response.</summary>
    public async Task<DeviceCodeResponse?> InitiateDeviceFlowAsync(CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.PostAsync(
            DeviceCodeUrl,
            new FormUrlEncodedContent([
                new("client_id", ClientId),
                new("scope", Scopes)
            ]),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct).ConfigureAwait(false);
    }

    /// <summary>Polls GitHub for access token. Returns token if granted, null if pending.</summary>
    public async Task<string?> PollForTokenAsync(string deviceCode, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.PostAsync(
            TokenUrl,
            new FormUrlEncodedContent([
                new("client_id", ClientId),
                new("device_code", deviceCode),
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            ]),
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        if (json is null)
            return null;

        if (json.TryGetPropertyValue("access_token", out var tokenNode) && tokenNode is JsonValue)
        {
            var token = tokenNode.GetValue<string>();
            await StoreTokenAsync(token, ct).ConfigureAwait(false);
            return token;
        }

        return null; // authorization_pending or slow_down
    }

    /// <summary>Returns true if a GitHub token is stored.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>Retrieves the stored GitHub access token.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var config = await integrationStore.GetConfigAsync(IntegrationId, ct).ConfigureAwait(false);
        if (config is null)
            return null;

        return config.TryGetPropertyValue("access_token", out var node) && node is JsonValue
            ? node.GetValue<string>()
            : null;
    }

    /// <summary>Removes stored GitHub token (disconnect).</summary>
    public async Task DisconnectAsync(CancellationToken ct = default) =>
        await integrationStore.RemoveConfigAsync(IntegrationId, ct).ConfigureAwait(false);

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task StoreTokenAsync(string token, CancellationToken ct)
    {
        var config = new JsonObject { ["access_token"] = token };
        await integrationStore.SetConfigAsync(IntegrationId, config, ct).ConfigureAwait(false);
    }
}

/// <summary>Response from GitHub's device code endpoint.</summary>
public sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);
