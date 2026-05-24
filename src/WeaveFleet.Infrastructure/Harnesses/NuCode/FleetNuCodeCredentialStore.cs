using NuCode.Providers;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Fleet implementation of <see cref="INuCodeCredentialStore"/>.
/// Adapts Fleet's <see cref="IUserCredentialRepository"/> and <see cref="ICredentialProtector"/>
/// to NuCode's portable credential storage interface.
///
/// Mapping convention:
///   NuCode providerId  → UserCredential.Namespace  (prefixed with "nucode:")
///   NuCode fieldKey    → UserCredential.Kind
///   Label              → "nucode:{providerId}:{fieldKey}" (unique per user)
/// </summary>
internal sealed class FleetNuCodeCredentialStore(
    IUserCredentialRepository repository,
    ICredentialProtector protector,
    IUserContext userContext) : INuCodeCredentialStore
{
    private const string NamespacePrefix = "nucode:";

    /// <inheritdoc />
    public async Task<StoredCredential?> GetAsync(
        string providerId,
        string fieldKey,
        CancellationToken ct = default)
    {
        var ns = ToNamespace(providerId);
        var creds = await repository.ListByUserNamespaceAndKindAsync(ns, fieldKey).ConfigureAwait(false);
        var match = creds.Count > 0 ? creds[0] : null;
        return match is null ? null : ToStoredCredential(match, providerId, fieldKey);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredCredential>> GetAllForProviderAsync(
        string providerId,
        CancellationToken ct = default)
    {
        var ns = ToNamespace(providerId);
        var creds = await repository.ListByUserAndNamespaceAsync(ns).ConfigureAwait(false);
        return creds
            .Select(c => ToStoredCredential(c, providerId, c.Kind))
            .ToList();
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string providerId,
        string fieldKey,
        string value,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("O");
        var encrypted = protector.Encrypt(value);
        var displayHint = ComputeDisplayHint(value);
        var metadata = expiresAt.HasValue
            ? $"{{\"expires_at\":\"{expiresAt.Value:O}\"}}"
            : null;

        var credential = new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userContext.UserId,
            Namespace = ToNamespace(providerId),
            Kind = fieldKey,
            Label = ToLabel(providerId, fieldKey),
            EncryptedValue = encrypted,
            DisplayHint = displayHint,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.UpsertAsync(credential).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string providerId,
        string fieldKey,
        CancellationToken ct = default)
    {
        var ns = ToNamespace(providerId);
        var creds = await repository.ListByUserNamespaceAndKindAsync(ns, fieldKey).ConfigureAwait(false);
        foreach (var cred in creds)
        {
            await repository.DeleteAsync(cred.Id).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAllForProviderAsync(
        string providerId,
        CancellationToken ct = default)
    {
        var ns = ToNamespace(providerId);
        var creds = await repository.ListByUserAndNamespaceAsync(ns).ConfigureAwait(false);
        foreach (var cred in creds)
        {
            await repository.DeleteAsync(cred.Id).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListConfiguredProviderIdsAsync(
        CancellationToken ct = default)
    {
        var all = await repository.ListByUserAsync().ConfigureAwait(false);
        return all
            .Where(c => c.Namespace.StartsWith(NamespacePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Namespace[NamespacePrefix.Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private StoredCredential ToStoredCredential(UserCredential cred, string providerId, string fieldKey)
    {
        var decrypted = protector.Decrypt(cred.EncryptedValue);
        DateTimeOffset? expiresAt = null;

        if (cred.Metadata is not null)
        {
            expiresAt = TryParseExpiry(cred.Metadata);
        }

        return new StoredCredential(
            ProviderId: providerId,
            FieldKey: fieldKey,
            Value: decrypted,
            ExpiresAt: expiresAt,
            DisplayHint: cred.DisplayHint);
    }

    private static string ToNamespace(string providerId) =>
        $"{NamespacePrefix}{providerId.ToLowerInvariant()}";

    private static string ToLabel(string providerId, string fieldKey) =>
        $"nucode:{providerId.ToLowerInvariant()}:{fieldKey.ToLowerInvariant()}";

    private static string ComputeDisplayHint(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= 4
            ? new string('*', value.Length)
            : $"...{value[^4..]}";
    }

    private static DateTimeOffset? TryParseExpiry(string metadata)
    {
        try
        {
            // Simple JSON parse for {"expires_at":"..."}
            const string key = "\"expires_at\":\"";
            var start = metadata.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return null;
            start += key.Length;
            var end = metadata.IndexOf('"', start);
            if (end < 0) return null;
            var raw = metadata[start..end];
            return DateTimeOffset.TryParse(raw, out var dt) ? dt : null;
        }
        catch
        {
            return null;
        }
    }
}
