namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A stored, encrypted user credential identified by namespace/provider, kind, and user-chosen label.
/// Multiple credentials per user are supported — there is no unique constraint on (UserId, Namespace, Kind).
/// The unique constraint is on (UserId, Label) to prevent ambiguous display names.
/// </summary>
public sealed class UserCredential
{
    /// <summary>Stable GUID identifier for this credential record.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Provider/system namespace — e.g. "anthropic", "openai", "custom".
    /// This is a domain concept, not an environment variable name.
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Credential type within the namespace — e.g. "api-key", "oauth-token".
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// User-chosen display name for this credential — e.g. "My Anthropic Key", "Work OpenAI".
    /// Unique per user.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted credential value. Decrypted only when needed by the harness runtime preparation.
    /// Never returned in API responses.
    /// </summary>
    public string EncryptedValue { get; set; } = string.Empty;

    /// <summary>
    /// Display hint derived from the plaintext credential — typically the last 4 characters.
    /// Safe to return in API responses.
    /// </summary>
    public string DisplayHint { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON string for extensible key-value metadata (e.g. expiry, notes).
    /// Not interpreted by the application layer.
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>ISO 8601 creation timestamp.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>ISO 8601 last-updated timestamp.</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
