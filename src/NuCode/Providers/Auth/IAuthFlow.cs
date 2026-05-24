namespace NuCode.Providers.Auth;

/// <summary>
/// Executes the authentication flow for a provider.
/// Different providers use different flows (API key entry, OAuth device, OAuth browser, etc.).
/// </summary>
public interface IAuthFlow
{
    /// <summary>
    /// Initiates the auth flow for the given provider.
    /// </summary>
    /// <param name="provider">The provider definition describing the auth mechanism.</param>
    /// <param name="store">The credential store to persist credentials into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="AuthFlowResult"/> indicating whether the flow succeeded, needs user action,
    /// or failed.
    /// </returns>
    Task<AuthFlowResult> ExecuteAsync(
        ProviderDefinition provider,
        INuCodeCredentialStore store,
        CancellationToken ct = default);
}
