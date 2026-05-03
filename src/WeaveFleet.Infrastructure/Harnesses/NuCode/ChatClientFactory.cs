using Microsoft.Extensions.AI;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Factory that creates <see cref="IChatClient"/> instances from provider name and API key.
/// Isolates LLM SDK dependencies from the rest of the harness.
/// </summary>
internal static class ChatClientFactory
{
    private static readonly Uri CopilotEndpoint = new("https://api.githubcopilot.com/");

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for the specified provider and model.
    /// </summary>
    /// <param name="provider">Provider identifier (e.g. "anthropic", "openai", "copilot").</param>
    /// <param name="modelId">The model identifier (e.g. "claude-sonnet-4-20250514", "gpt-4o").</param>
    /// <param name="apiKey">The API key or token for the provider.</param>
    /// <returns>A configured <see cref="IChatClient"/>.</returns>
    public static IChatClient Create(string provider, string modelId, string apiKey)
    {
        return provider.ToLowerInvariant() switch
        {
            "anthropic" => CreateAnthropicClient(modelId, apiKey),
            "openai" => CreateOpenAIClient(modelId, apiKey),
            "copilot" => CreateCopilotClient(modelId, apiKey),
            _ => throw new NotSupportedException($"LLM provider '{provider}' is not supported by the NuCode harness."),
        };
    }

    private static IChatClient CreateAnthropicClient(string modelId, string apiKey)
    {
        // Use OpenAI-compatible endpoint via Microsoft.Extensions.AI.OpenAI
        // Anthropic doesn't have a native MEAI package yet — use the OpenAI adapter
        // pointed at Anthropic's API.
        // TODO: Replace with native Anthropic MEAI adapter when available.
        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri("https://api.anthropic.com/v1/") });
        return client.GetChatClient(modelId).AsIChatClient();
    }

    private static IChatClient CreateOpenAIClient(string modelId, string apiKey)
    {
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey));
        return client.GetChatClient(modelId).AsIChatClient();
    }

    private static IChatClient CreateCopilotClient(string modelId, string copilotToken)
    {
        // GitHub Copilot exposes an OpenAI-compatible chat completions endpoint.
        // The copilotToken is a short-lived token obtained by exchanging the user's
        // GitHub OAuth token via CopilotTokenService.
        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(copilotToken),
            new OpenAI.OpenAIClientOptions { Endpoint = CopilotEndpoint });
        return client.GetChatClient(modelId).AsIChatClient();
    }
}
