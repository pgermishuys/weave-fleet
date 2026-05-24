using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NuCode.Providers.Auth;

/// <summary>
/// Exchanges a GitHub OAuth token for a short-lived GitHub Copilot API token.
/// The Copilot token is then used as a Bearer token against the OpenAI-compatible
/// endpoint at <c>https://api.githubcopilot.com</c>.
/// </summary>
public static class CopilotTokenExchange
{
    private const string TokenExchangeUrl = "https://api.github.com/copilot_internal/v2/token";

    /// <summary>
    /// Exchanges a GitHub OAuth access token for a short-lived Copilot API token.
    /// </summary>
    /// <param name="httpClient">HTTP client for making the exchange request.</param>
    /// <param name="gitHubToken">The user's GitHub OAuth access token (must have Copilot access).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The short-lived Copilot API token response.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the exchange fails or the user lacks a Copilot subscription.</exception>
    public static async Task<CopilotTokenResponse> ExchangeAsync(
        INuCodeHttpClient httpClient,
        string gitHubToken,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenExchangeUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", gitHubToken);
        request.Headers.UserAgent.ParseAdd("NuCode/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Failed to exchange GitHub token for Copilot API token. " +
                $"HTTP {(int)response.StatusCode}: {body}. " +
                $"Ensure your GitHub account has an active Copilot subscription.");
        }

        var tokenResponse = await response.Content
            .ReadFromJsonAsync(CopilotTokenResponseContext.Default.CopilotTokenResponse, ct)
            .ConfigureAwait(false);

        return tokenResponse
            ?? throw new InvalidOperationException("Copilot token exchange returned an empty response.");
    }
}

/// <summary>Response from the GitHub Copilot token exchange endpoint.</summary>
public sealed record CopilotTokenResponse
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; init; }
}

[JsonSerializable(typeof(CopilotTokenResponse))]
internal sealed partial class CopilotTokenResponseContext : JsonSerializerContext;
