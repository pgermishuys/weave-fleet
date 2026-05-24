using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Opaque launch artifacts for the NuCode harness.
/// Carries the resolved provider, model, and credentials from PrepareRuntimeAsync to SpawnAsync.
/// Credentials are stored as a generic dictionary (fieldKey → decrypted value) so that
/// new providers can be added without changing this type.
/// </summary>
/// <param name="ProviderId">Provider identifier (e.g. "anthropic", "openai", "copilot").</param>
/// <param name="ModelId">The model identifier.</param>
/// <param name="Credentials">Resolved credentials for the provider (fieldKey → decrypted value).</param>
/// <param name="ProviderOptions">Optional provider-level config (e.g. "baseUrl", "resourceName").</param>
public sealed record NuCodeLaunchArtifacts(
    string ProviderId,
    string ModelId,
    IReadOnlyDictionary<string, string> Credentials,
    IReadOnlyDictionary<string, string>? ProviderOptions = null) : RuntimeLaunchArtifacts;
