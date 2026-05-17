namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Preference key constants for NuCode harness configuration.
/// </summary>
internal static class NuCodePreferenceKeys
{
    /// <summary>Whether NuCode is enabled. Value: "true" / "false".</summary>
    internal const string Enabled = "nucode.enabled";

    /// <summary>LLM provider. Value: "anthropic", "openai", "copilot", or "custom".</summary>
    internal const string Provider = "nucode.provider";

    /// <summary>Model identifier (e.g. "claude-sonnet-4-20250514").</summary>
    internal const string ModelId = "nucode.modelId";

    /// <summary>Optional custom base URL for OpenAI-compatible endpoints.</summary>
    internal const string BaseUrl = "nucode.baseUrl";
}
