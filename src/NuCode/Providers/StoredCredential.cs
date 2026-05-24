namespace NuCode.Providers;

/// <summary>
/// A single stored credential value for a provider field.
/// Values are always decrypted when returned — encryption is the host's responsibility.
/// </summary>
/// <param name="ProviderId">The provider this credential belongs to (e.g. "anthropic").</param>
/// <param name="FieldKey">The credential field key (e.g. "apiKey", "resourceName").</param>
/// <param name="Value">The decrypted credential value.</param>
/// <param name="ExpiresAt">Optional expiry time (e.g. for short-lived OAuth tokens).</param>
/// <param name="DisplayHint">Safe-to-display hint (e.g. last 4 chars of an API key).</param>
public sealed record StoredCredential(
    string ProviderId,
    string FieldKey,
    string Value,
    DateTimeOffset? ExpiresAt = null,
    string? DisplayHint = null);
