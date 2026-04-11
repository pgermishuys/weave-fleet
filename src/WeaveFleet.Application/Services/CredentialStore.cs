using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Application-layer implementation of <see cref="ICredentialStore"/>.
/// Manages encrypt-on-write, decrypt-on-read for user credentials.
/// Pure storage concern — does not interpret credential values, build env-var dictionaries,
/// or perform any harness-specific logic.
/// </summary>
public sealed class CredentialStore(
    IUserCredentialRepository credentialRepository,
    ICredentialProtector credentialProtector,
    IUserContext userContext) : ICredentialStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialSummary>> ListCredentialsAsync()
    {
        var credentials = await credentialRepository.ListByUserAsync();
        return credentials
            .Select(c => new CredentialSummary(
                c.Id,
                c.Label,
                c.Namespace,
                c.Kind,
                c.DisplayHint,
                c.Metadata,
                c.CreatedAt,
                c.UpdatedAt))
            .ToList();
    }

    /// <inheritdoc />
    public async Task StoreCredentialAsync(
        string label,
        string credentialNamespace,
        string kind,
        string value,
        string? metadata = null)
    {
        var now = DateTime.UtcNow.ToString("O");
        var encrypted = credentialProtector.Encrypt(value);
        var displayHint = ComputeDisplayHint(value);

        var credential = new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userContext.UserId,
            Namespace = credentialNamespace,
            Kind = kind,
            Label = label,
            EncryptedValue = encrypted,
            DisplayHint = displayHint,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now
        };

        await credentialRepository.UpsertAsync(credential);
    }

    /// <inheritdoc />
    public async Task DeleteCredentialAsync(string id)
    {
        await credentialRepository.DeleteAsync(id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserCredential>> GetDecryptedCredentialsAsync(string userId)
    {
        // Use the system user context workaround: the repository uses IUserContext.UserId,
        // but GetDecryptedCredentialsAsync is called by the orchestrator using the session owner's userId.
        // For now, use a direct query pattern — but since the repo is scoped to IUserContext.UserId,
        // we accept that in cloud mode the orchestrator calls this within the request scope where
        // userContext.UserId == session.UserId (own session) or session.UserId (owner credentials for resume).
        // The userId parameter is kept for future validation.
        var credentials = await credentialRepository.ListByUserAsync();
        return credentials
            .Select(c =>
            {
                c.EncryptedValue = credentialProtector.Decrypt(c.EncryptedValue);
                return c;
            })
            .ToList();
    }

    private static string ComputeDisplayHint(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Return last 4 characters for display (common API key hint pattern)
        return value.Length <= 4
            ? new string('*', value.Length)
            : $"...{value[^4..]}";
    }
}
