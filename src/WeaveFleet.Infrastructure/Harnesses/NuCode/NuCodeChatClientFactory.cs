using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using NuCode.Providers;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Data-driven <see cref="IChatClientFactory"/> implementation.
/// Uses <see cref="ProviderDefinition"/> to create OpenAI-compatible chat clients.
/// All providers that are OpenAI-compatible use the same client creation path —
/// just different endpoints and credentials.
/// </summary>
internal sealed class NuCodeChatClientFactory : IChatClientFactory
{
    private static readonly Uri CopilotEndpoint = new("https://api.githubcopilot.com/");

    private const string ApiKeyField = "apiKey";
    private const string GitHubTokenField = "githubToken";
    private const string BaseUrlOption = "baseUrl";
    private const string ResourceNameOption = "resourceName";

    /// <inheritdoc />
    public IChatClient Create(
        ProviderDefinition provider,
        string modelId,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, string>? options = null)
    {
        if (!provider.IsOpenAiCompatible)
        {
            throw new NotSupportedException(
                $"Provider '{provider.Id}' is not OpenAI-compatible and cannot be used with NuCodeChatClientFactory.");
        }

        var endpoint = ResolveEndpoint(provider, options);
        var apiKey = ResolveApiKey(provider, credentials);

        var credential = string.IsNullOrEmpty(apiKey)
            ? new System.ClientModel.ApiKeyCredential("no-key")
            : new System.ClientModel.ApiKeyCredential(apiKey);

        var clientOptions = new OpenAI.OpenAIClientOptions();
        if (endpoint is not null)
            clientOptions.Endpoint = endpoint;

        // Copilot requires specific headers for API access
        if (string.Equals(provider.Id, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            clientOptions.AddPolicy(new CopilotHeaderPolicy(), PipelinePosition.PerCall);
        }

        var client = new OpenAI.OpenAIClient(credential, clientOptions);
        return client.GetChatClient(modelId).AsIChatClient();
    }

    private static Uri? ResolveEndpoint(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string>? options)
    {
        // Explicit baseUrl override from options
        if (options is not null && options.TryGetValue(BaseUrlOption, out var overrideUrl)
            && !string.IsNullOrWhiteSpace(overrideUrl))
        {
            return new Uri(overrideUrl);
        }

        // Azure: construct from resource name
        if (string.Equals(provider.Id, "azure-openai", StringComparison.OrdinalIgnoreCase)
            && options is not null
            && options.TryGetValue(ResourceNameOption, out var resourceName)
            && !string.IsNullOrWhiteSpace(resourceName))
        {
            return new Uri($"https://{resourceName}.openai.azure.com/");
        }

        if (string.Equals(provider.Id, "azure-cognitive-services", StringComparison.OrdinalIgnoreCase)
            && options is not null
            && options.TryGetValue(ResourceNameOption, out var cogResourceName)
            && !string.IsNullOrWhiteSpace(cogResourceName))
        {
            return new Uri($"https://{cogResourceName}.cognitiveservices.azure.com/");
        }

        // Copilot always uses its fixed endpoint
        if (string.Equals(provider.Id, "copilot", StringComparison.OrdinalIgnoreCase))
            return CopilotEndpoint;

        // Use provider's default endpoint if set
        if (provider.DefaultEndpoint is not null)
            return new Uri(provider.DefaultEndpoint);

        // OpenAI uses SDK default (null = let SDK decide)
        return null;
    }

    private static string ResolveApiKey(
        ProviderDefinition provider,
        IReadOnlyDictionary<string, string> credentials)
    {
        // Copilot uses the GitHub OAuth token directly as a Bearer token
        if (string.Equals(provider.Id, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            return credentials.TryGetValue(GitHubTokenField, out var githubToken)
                ? githubToken
                : string.Empty;
        }

        return credentials.TryGetValue(ApiKeyField, out var apiKey) ? apiKey : string.Empty;
    }
}

/// <summary>
/// Pipeline policy that adds required headers for GitHub Copilot API requests.
/// </summary>
internal sealed class CopilotHeaderPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddCopilotHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddCopilotHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void AddCopilotHeaders(PipelineMessage message)
    {
        var request = message.Request;
        request.Headers.Set("Copilot-Integration-Id", "vscode-chat");
        request.Headers.Set("Editor-Version", "NuCode/1.0");
        request.Headers.Set("Editor-Plugin-Version", "NuCode/1.0");
        request.Headers.Set("Openai-Intent", "conversation-edits");
    }
}
