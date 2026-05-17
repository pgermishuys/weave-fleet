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
    /// <param name="provider">Provider identifier (e.g. "anthropic", "openai", "copilot", "custom").</param>
    /// <param name="modelId">The model identifier (e.g. "claude-sonnet-4-20250514", "gpt-4o").</param>
    /// <param name="apiKey">The API key or token for the provider.</param>
    /// <param name="baseUrl">Optional custom base URL for OpenAI-compatible endpoints.</param>
    /// <returns>A configured <see cref="IChatClient"/>.</returns>
    public static IChatClient Create(string provider, string modelId, string apiKey, string? baseUrl = null)
    {
        return provider.ToLowerInvariant() switch
        {
            "anthropic" => CreateAnthropicClient(modelId, apiKey, baseUrl),
            "openai" => CreateOpenAIClient(modelId, apiKey, baseUrl),
            "copilot" => CreateCopilotClient(modelId, apiKey),
            "custom" => CreateCustomClient(modelId, apiKey, baseUrl),
            _ => throw new NotSupportedException($"LLM provider '{provider}' is not supported by the NuCode harness."),
        };
    }

    private static IChatClient CreateAnthropicClient(string modelId, string apiKey, string? baseUrl)
    {
        var endpoint = baseUrl is not null ? new Uri(baseUrl) : new Uri("https://api.anthropic.com/v1/");
        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAI.OpenAIClientOptions { Endpoint = endpoint });
        return client.GetChatClient(modelId).AsIChatClient();
    }

    private static IChatClient CreateOpenAIClient(string modelId, string apiKey, string? baseUrl)
    {
        if (baseUrl is not null)
        {
            var client = new OpenAI.OpenAIClient(
                new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
            return client.GetChatClient(modelId).AsIChatClient();
        }
        else
        {
            var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey));
            return client.GetChatClient(modelId).AsIChatClient();
        }
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

    private static IChatClient CreateCustomClient(string modelId, string apiKey, string? baseUrl)
    {
        // OpenAI-compatible endpoint (e.g. Ollama, OpenRouter, Helicone proxy).
        // baseUrl is required; apiKey may be empty for local models.
        var endpoint = baseUrl is not null
            ? new Uri(baseUrl)
            : throw new InvalidOperationException("A base URL is required for the 'custom' provider.");
        var credential = string.IsNullOrEmpty(apiKey)
            ? new System.ClientModel.ApiKeyCredential("no-key")
            : new System.ClientModel.ApiKeyCredential(apiKey);
        var client = new OpenAI.OpenAIClient(
            credential,
            new OpenAI.OpenAIClientOptions { Endpoint = endpoint });
        return client.GetChatClient(modelId).AsIChatClient();
    }
}
