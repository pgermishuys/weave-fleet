using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// GitHub OAuth device flow + token management.
/// </summary>
public sealed class GitHubService(
    IHttpClientFactory httpClientFactory,
    IPluginStateStore pluginStateStore)
{
    private const string IntegrationId = "github";
    private const string ClientId = "Ov23liJT2Q0HXHj9xLGM";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string UserUrl = "https://api.github.com/user";
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

    /// <summary>Polls GitHub for access token or terminal device-flow status.</summary>
    public async Task<DeviceFlowPollResult> PollForTokenAsync(string userId, string deviceCode, CancellationToken ct = default)
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

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        if (json is null)
            return new DeviceFlowPollResult(DeviceFlowPollStatus.Error, Message: "GitHub returned an empty response.");

        if (json.TryGetPropertyValue("access_token", out var tokenNode) && tokenNode is JsonValue)
        {
            var token = tokenNode.GetValue<string>();
            await StoreTokenAsync(userId, token, ct).ConfigureAwait(false);
            return new DeviceFlowPollResult(DeviceFlowPollStatus.Complete);
        }

        var error = json["error"]?.GetValue<string>();
        var interval = json["interval"]?.GetValue<int?>();

        return error switch
        {
            "authorization_pending" => new DeviceFlowPollResult(DeviceFlowPollStatus.Pending, Interval: interval),
            "slow_down" => new DeviceFlowPollResult(DeviceFlowPollStatus.Pending, Interval: interval, Message: "GitHub requested slower polling."),
            "expired_token" => new DeviceFlowPollResult(DeviceFlowPollStatus.Expired),
            "access_denied" => new DeviceFlowPollResult(DeviceFlowPollStatus.Denied),
            null when !response.IsSuccessStatusCode => new DeviceFlowPollResult(DeviceFlowPollStatus.Error, Message: $"GitHub poll failed with HTTP {(int)response.StatusCode}."),
            null => new DeviceFlowPollResult(DeviceFlowPollStatus.Pending, Interval: interval),
            _ => new DeviceFlowPollResult(DeviceFlowPollStatus.Error, Interval: interval, Message: json["error_description"]?.GetValue<string>() ?? error),
        };
    }

    /// <summary>Returns true if a GitHub token is stored for the given user.</summary>
    public async Task<bool> IsConnectedAsync(string userId, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(userId, ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>Retrieves the stored GitHub access token for the given user.</summary>
    public async Task<string?> GetTokenAsync(string userId, CancellationToken ct = default)
    {
        var config = await pluginStateStore.GetStateAsync(IntegrationId, userId, ct).ConfigureAwait(false);
        if (config is null)
            return null;

        return config.TryGetPropertyValue("access_token", out var node) && node is JsonValue
            ? node.GetValue<string>()
            : null;
    }

    public async Task<bool> ConnectWithTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("weave-fleet/1.0");

        var response = await client.GetAsync(UserUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return false;

        await StoreTokenAsync(userId, token, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Removes stored GitHub token (disconnect) for the given user.</summary>
    public async Task DisconnectAsync(string userId, CancellationToken ct = default) =>
        await pluginStateStore.RemoveStateAsync(IntegrationId, userId, ct).ConfigureAwait(false);

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task StoreTokenAsync(string userId, string token, CancellationToken ct)
    {
        var config = new JsonObject
        {
            ["access_token"] = token,
            ["connected_at"] = DateTimeOffset.UtcNow,
        };

        await pluginStateStore.SetStateAsync(IntegrationId, userId, config, ct).ConfigureAwait(false);
    }
}

/// <summary>Response from GitHub's device code endpoint.</summary>
public sealed record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")]
    string DeviceCode,

    [property: JsonPropertyName("user_code")]
    string UserCode,

    [property: JsonPropertyName("verification_uri")]
    string VerificationUri,

    [property: JsonPropertyName("expires_in")]
    int ExpiresIn,

    [property: JsonPropertyName("interval")]
    int Interval);

public sealed record DeviceFlowPollResult(
    DeviceFlowPollStatus Status,
    int? Interval = null,
    string? Message = null);

public enum DeviceFlowPollStatus
{
    Pending = 0,
    Complete = 1,
    Expired = 2,
    Denied = 3,
    Error = 4,
}
