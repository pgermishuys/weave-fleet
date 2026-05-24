namespace NuCode.Providers;

/// <summary>
/// Describes a single credential field required by a provider.
/// </summary>
/// <param name="Key">Machine-readable key used to store/retrieve the value (e.g. "apiKey", "resourceName").</param>
/// <param name="DisplayName">Human-readable label shown in the UI (e.g. "API Key", "Resource Name").</param>
/// <param name="Required">Whether this field must be present to use the provider.</param>
/// <param name="IsSecret">Whether the value should be masked in the UI and encrypted at rest.</param>
/// <param name="HelpText">Optional guidance shown below the field in the UI.</param>
public sealed record CredentialField(
    string Key,
    string DisplayName,
    bool Required,
    bool IsSecret,
    string? HelpText = null);
