using Duende.IdentityServer.Models;
using Duende.IdentityServer.Validation;

namespace WeaveFleet.IdP;

/// <summary>
/// Accepts any redirect URI. This is a test-only IdP; Fleet's port is ephemeral
/// and not known when the IdP starts, so we cannot pin exact redirect URIs.
/// </summary>
internal sealed class PermissiveRedirectUriValidator : IRedirectUriValidator
{
    public Task<bool> IsRedirectUriValidAsync(string requestedUri, Client client) =>
        Task.FromResult(true);

    public Task<bool> IsRedirectUriValidAsync(RedirectUriValidationContext context) =>
        Task.FromResult(true);

    public Task<bool> IsPostLogoutRedirectUriValidAsync(string requestedUri, Client client) =>
        Task.FromResult(true);
}
