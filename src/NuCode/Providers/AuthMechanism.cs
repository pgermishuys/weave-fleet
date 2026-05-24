namespace NuCode.Providers;

/// <summary>
/// Describes how a provider authenticates users.
/// </summary>
public enum AuthMechanism
{
    /// <summary>A static API key entered by the user.</summary>
    ApiKey,

    /// <summary>OAuth device flow (e.g. GitHub Copilot — user visits a URL and enters a code).</summary>
    OAuthDevice,

    /// <summary>OAuth browser flow (e.g. OpenAI ChatGPT Plus — browser redirect).</summary>
    OAuthBrowser,

    /// <summary>AWS credential chain (access keys, named profile, instance metadata, etc.).</summary>
    AwsCredentialChain,

    /// <summary>Service account file or gcloud CLI (e.g. Google Vertex AI).</summary>
    ServiceAccountFile,

    /// <summary>No authentication required (e.g. local Ollama, llama.cpp).</summary>
    None,

    /// <summary>Custom / user-defined authentication.</summary>
    Custom,
}
