using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Exchanges a GitHub OAuth token for a short-lived GitHub Copilot API token.
/// The Copilot token is then used as a Bearer token against the OpenAI-compatible
/// endpoint at <c>https://api.githubcopilot.com</c>.
/// </summary>
internal static class CopilotTokenService
{
    private const string TokenExchangeUrl = "https://api.github.com/copilot_internal/v2/token";

    /// <summary>
    /// Exchanges a GitHub OAuth access token for a short-lived Copilot API token.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating HTTP clients.</param>
    /// <param name="gitHubToken">The user's GitHub OAuth access token (must have Copilot access).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The short-lived Copilot API token, or throws if the exchange fails.</returns>
    public static async Task<CopilotTokenResponse> ExchangeAsync(
        IHttpClientFactory httpClientFactory,
        string gitHubToken,
        CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", gitHubToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WeaveFleet-NuCode/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.GetAsync(TokenExchangeUrl, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Failed to exchange GitHub token for Copilot API token. " +
                $"HTTP {(int)response.StatusCode}: {body}. " +
                $"Ensure your GitHub account has an active Copilot subscription.");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync(
            NuCodeJsonContext.Default.CopilotTokenResponse, ct).ConfigureAwait(false);

        return tokenResponse ?? throw new InvalidOperationException("Copilot token exchange returned an empty response.");
    }
}

/// <summary>
/// Response from the GitHub Copilot token exchange endpoint.
/// </summary>
internal sealed record CopilotTokenResponse
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; init; }
}
