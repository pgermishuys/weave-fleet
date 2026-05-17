using Microsoft.Extensions.AI;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Tests NuCode connectivity by performing a minimal chat completion with the configured provider.
/// </summary>
internal sealed class NuCodeConnectionTester : INuCodeConnectionTester
{
    private readonly IUserPreferenceRepository _prefs;
    private readonly IUserCredentialRepository _credentials;
    private readonly IHttpClientFactory _httpClientFactory;

    public NuCodeConnectionTester(
        IUserPreferenceRepository prefs,
        IUserCredentialRepository credentials,
        IHttpClientFactory httpClientFactory)
    {
        _prefs = prefs;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<NuCodeConnectionTestResult> TestAsync(CancellationToken ct)
    {
        var provider = await _prefs.GetAsync(NuCodePreferenceKeys.Provider).ConfigureAwait(false) ?? "copilot";
        var modelId = await _prefs.GetAsync(NuCodePreferenceKeys.ModelId).ConfigureAwait(false) ?? "claude-sonnet-4-20250514";
        var baseUrl = await _prefs.GetAsync(NuCodePreferenceKeys.BaseUrl).ConfigureAwait(false);

        var (credNamespace, credKind) = ResolveCredentialLookup(provider);

        string apiKeyOrToken;
        var creds = await _credentials.ListByUserNamespaceAndKindAsync(credNamespace, credKind).ConfigureAwait(false);

        if (creds.Count == 0 && !IsApiKeyOptional(provider))
        {
            return new NuCodeConnectionTestResult(
                Success: false,
                Error: $"No credentials found for provider '{provider}'. Add them in Settings → Credentials.",
                LatencyMs: 0);
        }

        apiKeyOrToken = creds.Count > 0 ? creds[0].EncryptedValue : string.Empty;

        if (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var copilotToken = await CopilotTokenService.ExchangeAsync(
                    _httpClientFactory, apiKeyOrToken, ct).ConfigureAwait(false);
                apiKeyOrToken = copilotToken.Token;
            }
            catch (Exception ex)
            {
                return new NuCodeConnectionTestResult(
                    Success: false,
                    Error: $"Failed to exchange GitHub token: {ex.Message}",
                    LatencyMs: 0);
            }
        }

        IChatClient? chatClient = null;
        try
        {
            chatClient = ChatClientFactory.Create(provider, modelId, apiKeyOrToken, baseUrl);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                cancellationToken: ct).ConfigureAwait(false);
            sw.Stop();

            return new NuCodeConnectionTestResult(Success: true, Error: null, LatencyMs: (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new NuCodeConnectionTestResult(Success: false, Error: ex.Message, LatencyMs: 0);
        }
        finally
        {
            chatClient?.Dispose();
        }
    }

    private static bool IsApiKeyOptional(string provider) =>
        string.Equals(provider, "custom", StringComparison.OrdinalIgnoreCase);

    private static (string Namespace, string Kind) ResolveCredentialLookup(string provider) =>
        provider.ToLowerInvariant() switch
        {
            "copilot" => ("github", "oauth-access-token"),
            "anthropic" => ("anthropic", "api-key"),
            "openai" => ("openai", "api-key"),
            "custom" => ("custom", "api-key"),
            _ => ("github", "oauth-access-token"),
        };
}
