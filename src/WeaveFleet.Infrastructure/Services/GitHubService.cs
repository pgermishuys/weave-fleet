using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// GitHub OAuth device flow + token management.
/// </summary>
public sealed class GitHubService(
    IHttpClientFactory httpClientFactory,
    IPluginStateStore pluginStateStore,
    IUserCredentialRepository credentialRepository,
    ICredentialProtector credentialProtector)
{
    private const string IntegrationId = "github";
    private const string CredentialNamespace = "github";
    private const string CredentialKind = "oauth-access-token";
    private const string CredentialLabel = "GitHub";
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
        return await response.Content.ReadFromJsonAsync(InfrastructureJsonContext.Default.DeviceCodeResponse, ct).ConfigureAwait(false);
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

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        if (json is null)
            return new DeviceFlowPollResult(DeviceFlowPollStatus.Error, Message: "GitHub returned an empty response.");

        if (json.TryGetPropertyValue("access_token", out var tokenNode) && tokenNode is JsonValue)
        {
            var token = tokenNode.GetValue<string>();
            await StoreTokenAsync(userId, token, "device-flow", ct).ConfigureAwait(false);
            await UpdateStoredLoginAsync(userId, token, ct).ConfigureAwait(false);
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

    public async Task<GitHubConnectionStatus> GetConnectionStatusAsync(string userId, CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(userId, ct).ConfigureAwait(false)
            ?? await MigrateLegacyTokenAsync(userId, ct).ConfigureAwait(false);

        if (credential is null)
            return new GitHubConnectionStatus(false, null);

        return new GitHubConnectionStatus(true, ReadConnectedAt(credential.Metadata));
    }

    /// <summary>Retrieves the stored GitHub access token for the given user.</summary>
    public async Task<string?> GetTokenAsync(string userId, CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(userId, ct).ConfigureAwait(false)
            ?? await MigrateLegacyTokenAsync(userId, ct).ConfigureAwait(false);

        if (credential is null)
            return null;

        return credentialProtector.Decrypt(credential.EncryptedValue);
    }

    public async Task<string?> GetGitHubLoginAsync(string userId, CancellationToken ct)
    {
        var credential = await GetCredentialAsync(userId, ct).ConfigureAwait(false)
            ?? await MigrateLegacyTokenAsync(userId, ct).ConfigureAwait(false);

        if (credential is null)
            return null;

        var login = ReadLogin(credential.Metadata);
        if (!string.IsNullOrWhiteSpace(login))
            return login;

        var token = credentialProtector.Decrypt(credential.EncryptedValue);
        var user = await GetGitHubUserAsync(token, ct).ConfigureAwait(false);
        login = ReadLogin(user);

        if (string.IsNullOrWhiteSpace(login))
            return null;

        credential.Metadata = UpdateMetadataLogin(credential.Metadata, login);
        credential.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");

        await credentialRepository.UpsertAsync(credential).ConfigureAwait(false);
        return login;
    }

    public async Task<bool> ConnectWithTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        var user = await GetGitHubUserAsync(token, ct).ConfigureAwait(false);
        if (user is null)
            return false;

        await StoreTokenAsync(userId, token, "manual-token", ReadLogin(user), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Removes stored GitHub token (disconnect) for the given user.</summary>
    public async Task DisconnectAsync(string userId, CancellationToken ct = default)
    {
        var credentials = await credentialRepository.ListByUserNamespaceAndKindAsync(userId, CredentialNamespace, CredentialKind).ConfigureAwait(false);
        foreach (var credential in credentials.Where(c => string.Equals(c.Label, CredentialLabel, StringComparison.Ordinal)))
        {
            await credentialRepository.DeleteAsync(credential.Id, userId).ConfigureAwait(false);
        }

        await RemoveLegacyStateAsync(userId, ct).ConfigureAwait(false);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task StoreTokenAsync(string userId, string token, string source, CancellationToken ct)
        => await StoreTokenAsync(userId, token, source, null, ct).ConfigureAwait(false);

    private async Task StoreTokenAsync(string userId, string token, string source, string? login, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var credential = new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Namespace = CredentialNamespace,
            Kind = CredentialKind,
            Label = CredentialLabel,
            EncryptedValue = credentialProtector.Encrypt(token),
            DisplayHint = ComputeDisplayHint(token),
            Metadata = CreateMetadata(now, source, login),
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O"),
        };

        await credentialRepository.UpsertAsync(credential).ConfigureAwait(false);
        await RemoveLegacyStateAsync(userId, ct).ConfigureAwait(false);
    }

    private async Task UpdateStoredLoginAsync(string userId, string token, CancellationToken ct)
    {
        var user = await GetGitHubUserAsync(token, ct).ConfigureAwait(false);
        var login = ReadLogin(user);
        if (string.IsNullOrWhiteSpace(login))
            return;

        var credential = await GetCredentialAsync(userId, ct).ConfigureAwait(false);
        if (credential is null)
            return;

        credential.Metadata = UpdateMetadataLogin(credential.Metadata, login);
        credential.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");

        await credentialRepository.UpsertAsync(credential).ConfigureAwait(false);
    }

    private async Task<UserCredential?> GetCredentialAsync(string userId, CancellationToken ct)
    {
        var credentials = await credentialRepository.ListByUserNamespaceAndKindAsync(userId, CredentialNamespace, CredentialKind).ConfigureAwait(false);
        return credentials.FirstOrDefault(c => string.Equals(c.Label, CredentialLabel, StringComparison.Ordinal));
    }

    private async Task<UserCredential?> MigrateLegacyTokenAsync(string userId, CancellationToken ct)
    {
        var legacyState = await pluginStateStore.GetStateAsync(IntegrationId, userId, ct).ConfigureAwait(false);
        if (legacyState is null)
            return null;

        if (!legacyState.TryGetPropertyValue("access_token", out var tokenNode) || tokenNode is not JsonValue)
            return null;

        var token = tokenNode.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var connectedAt = legacyState["connected_at"]?.GetValue<DateTimeOffset?>() ?? DateTimeOffset.UtcNow;
        var credential = new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Namespace = CredentialNamespace,
            Kind = CredentialKind,
            Label = CredentialLabel,
            EncryptedValue = credentialProtector.Encrypt(token),
            DisplayHint = ComputeDisplayHint(token),
            Metadata = CreateMetadata(connectedAt, "legacy-plugin-state"),
            CreatedAt = connectedAt.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        await credentialRepository.UpsertAsync(credential).ConfigureAwait(false);
        await RemoveLegacyStateAsync(userId, ct).ConfigureAwait(false);
        return await GetCredentialAsync(userId, ct).ConfigureAwait(false);
    }

    private async Task RemoveLegacyStateAsync(string userId, CancellationToken ct)
    {
        var legacyState = await pluginStateStore.GetStateAsync(IntegrationId, userId, ct).ConfigureAwait(false);
        if (legacyState is null)
            return;

        legacyState.Remove("access_token");
        legacyState.Remove("connected_at");

        if (legacyState.Count == 0)
        {
            await pluginStateStore.RemoveStateAsync(IntegrationId, userId, ct).ConfigureAwait(false);
            return;
        }

        await pluginStateStore.SetStateAsync(IntegrationId, userId, legacyState, ct).ConfigureAwait(false);
    }

    private static string CreateMetadata(DateTimeOffset connectedAt, string source)
        => CreateMetadata(connectedAt, source, null);

    private static string CreateMetadata(DateTimeOffset connectedAt, string source, string? login)
    {
        var metadata = new JsonObject
        {
            ["connected_at"] = connectedAt,
            ["source"] = source,
        };

        if (!string.IsNullOrWhiteSpace(login))
            metadata["login"] = login;

        return metadata.ToJsonString();
    }

    private async Task<JsonObject?> GetGitHubUserAsync(string token, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fleet/1.0");

        var response = await client.GetAsync(UserUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
    }

    private static string UpdateMetadataLogin(string? metadata, string login)
    {
        JsonObject json;

        if (string.IsNullOrWhiteSpace(metadata))
        {
            json = new JsonObject();
        }
        else
        {
            try
            {
                json = JsonNode.Parse(metadata) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                json = new JsonObject();
            }
        }

        json["login"] = login;
        return json.ToJsonString();
    }

    private static DateTimeOffset? ReadConnectedAt(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;

        try
        {
            var node = JsonNode.Parse(metadata) as JsonObject;
            return node?["connected_at"]?.GetValue<DateTimeOffset?>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadLogin(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;

        try
        {
            var node = JsonNode.Parse(metadata) as JsonObject;
            return ReadLogin(node);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadLogin(JsonObject? user)
        => user?["login"]?.GetValue<string?>();

    private static string ComputeDisplayHint(string value)
        => value.Length <= 4
            ? new string('*', value.Length)
            : $"...{value[^4..]}";
}

public sealed record GitHubConnectionStatus(bool Connected, DateTimeOffset? ConnectedAt);

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
