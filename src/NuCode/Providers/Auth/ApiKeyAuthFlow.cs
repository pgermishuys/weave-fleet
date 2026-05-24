namespace NuCode.Providers.Auth;

/// <summary>
/// Auth flow for API-key providers.
/// The credential is already stored by the time this flow runs (the UI collects and stores it).
/// This flow is a no-op — it simply confirms the credential is present.
/// </summary>
public sealed class ApiKeyAuthFlow : IAuthFlow
{
    private const string ApiKeyFieldKey = "apiKey";

    /// <inheritdoc />
    public async Task<AuthFlowResult> ExecuteAsync(
        ProviderDefinition provider,
        INuCodeCredentialStore store,
        CancellationToken ct = default)
    {
        var cred = await store.GetAsync(provider.Id, ApiKeyFieldKey, ct).ConfigureAwait(false);

        if (cred is null && !provider.CredentialOptional)
        {
            return new AuthFlowResult.Failed(
                $"No API key found for provider '{provider.DisplayName}'. " +
                "Store the API key before initiating the auth flow.");
        }

        return new AuthFlowResult.Success();
    }
}
