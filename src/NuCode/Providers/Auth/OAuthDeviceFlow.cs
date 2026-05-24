using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuCode.Providers.Auth;

/// <summary>
/// OAuth device flow implementation for GitHub Copilot.
/// Initiates the GitHub device authorization flow and polls for completion.
/// On success, stores the GitHub OAuth token in the credential store.
/// </summary>
public sealed class OAuthDeviceFlow : IAuthFlow
{
    // NuCode's own GitHub OAuth App client ID (public — device flow doesn't use a secret)
    private const string GitHubClientId = "Ov23liJT2Q0HXHj9xLGM";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string GitHubTokenFieldKey = "githubToken";

    private readonly INuCodeHttpClient _httpClient;

    public OAuthDeviceFlow(INuCodeHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<AuthFlowResult> ExecuteAsync(
        ProviderDefinition provider,
        INuCodeCredentialStore store,
        CancellationToken ct = default)
    {
        DeviceCodeResponse? deviceCode;
        try
        {
            deviceCode = await RequestDeviceCodeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Failed to initiate GitHub device flow: {ex.Message}");
        }

        var instructions =
            $"Visit https://github.com/login/device and enter code: {deviceCode.UserCode}";

        return new AuthFlowResult.NeedsUserAction(
            Instructions: instructions,
            PollAsync: pollCt => PollAsync(provider, store, deviceCode, pollCt));
    }

    private async Task<AuthFlowResult> PollAsync(
        ProviderDefinition provider,
        INuCodeCredentialStore store,
        DeviceCodeResponse deviceCode,
        CancellationToken ct)
    {
        string? accessToken;
        try
        {
            accessToken = await PollForTokenAsync(deviceCode.DeviceCode, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"GitHub device flow polling failed: {ex.Message}");
        }

        if (accessToken is null)
        {
            // Still pending — caller should poll again
            return new AuthFlowResult.NeedsUserAction(
                Instructions: $"Visit https://github.com/login/device and enter code: {deviceCode.UserCode}",
                PollAsync: pollCt => PollAsync(provider, store, deviceCode, pollCt));
        }

        await store.SetAsync(provider.Id, GitHubTokenFieldKey, accessToken, ct: ct).ConfigureAwait(false);
        return new AuthFlowResult.Success();
    }

    /// <summary>
    /// Requests a device code from GitHub. Returns structured data for the client to display.
    /// </summary>
    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", GitHubClientId),
            new KeyValuePair<string, string>("scope", "repo,read:user,read:org"),
        ]);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync(OAuthDeviceFlowJsonContext.Default.DeviceCodeResponse, ct)
            .ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("GitHub returned an empty device code response.");
    }

    /// <summary>
    /// Polls GitHub for token completion. Returns the access token if granted, null if still pending.
    /// Throws on expired/denied.
    /// </summary>
    /// <returns>The access token if granted, or null if still pending.</returns>
    public async Task<string?> PollForTokenAsync(string deviceCode, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", GitHubClientId),
            new KeyValuePair<string, string>("device_code", deviceCode),
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
        ]);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);
        var json = doc.RootElement;

        if (json.TryGetProperty("access_token", out var tokenProp))
            return tokenProp.GetString();

        if (json.TryGetProperty("error", out var errorProp))
        {
            var error = errorProp.GetString();
            return error switch
            {
                "authorization_pending" => null,
                "slow_down" => null,
                "expired_token" => throw new InvalidOperationException("The device code has expired. Please restart the flow."),
                "access_denied" => throw new InvalidOperationException("Authorization was denied by the user."),
                _ => throw new InvalidOperationException($"GitHub OAuth error: {error}"),
            };
        }

        return null;
    }
}

/// <summary>GitHub device code response.</summary>
public sealed record DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public required string DeviceCode { get; init; }

    [JsonPropertyName("user_code")]
    public required string UserCode { get; init; }

    [JsonPropertyName("verification_uri")]
    public required string VerificationUri { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("interval")]
    public int Interval { get; init; }
}

[JsonSerializable(typeof(DeviceCodeResponse))]
internal sealed partial class OAuthDeviceFlowJsonContext : JsonSerializerContext;
