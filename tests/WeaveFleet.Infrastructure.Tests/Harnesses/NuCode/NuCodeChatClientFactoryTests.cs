using Microsoft.Extensions.AI;
using NuCode.Providers;
using WeaveFleet.Infrastructure.Harnesses.NuCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.NuCode;

public sealed class NuCodeChatClientFactoryTests
{
    private static readonly ProviderRegistry Registry = new(BuiltInProviders.All());
    private static readonly NuCodeChatClientFactory Factory = new();

    private static ProviderDefinition Get(string id) =>
        Registry.GetById(id) ?? throw new InvalidOperationException($"Provider '{id}' not found");

    // ── API-key providers ─────────────────────────────────────────────────────

    [Fact]
    public void create_returns_chat_client_for_anthropic()
    {
        var provider = Get("anthropic");
        var credentials = new Dictionary<string, string> { ["apiKey"] = "sk-ant-test" };

        var client = Factory.Create(provider, "claude-sonnet-4-20250514", credentials);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void create_returns_chat_client_for_openai()
    {
        var provider = Get("openai");
        var credentials = new Dictionary<string, string> { ["apiKey"] = "sk-openai-test" };

        var client = Factory.Create(provider, "gpt-4o", credentials);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void create_returns_chat_client_for_copilot_with_copilot_token()
    {
        var provider = Get("copilot");
        var credentials = new Dictionary<string, string> { ["copilotToken"] = "ghu_test_token" };

        var client = Factory.Create(provider, "claude-sonnet-4-20250514", credentials);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void create_returns_chat_client_for_copilot_with_empty_credentials()
    {
        // Copilot token may not be present yet — factory should still return a client
        var provider = Get("copilot");
        var credentials = new Dictionary<string, string>();

        var client = Factory.Create(provider, "claude-sonnet-4-20250514", credentials);

        client.ShouldNotBeNull();
    }

    // ── Custom base URL ───────────────────────────────────────────────────────

    [Fact]
    public void create_uses_custom_base_url_from_options()
    {
        var provider = Get("openai");
        var credentials = new Dictionary<string, string> { ["apiKey"] = "sk-test" };
        var options = new Dictionary<string, string> { ["baseUrl"] = "http://localhost:11434/v1" };

        // Should not throw — custom endpoint is accepted
        var client = Factory.Create(provider, "llama3", credentials, options);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void create_uses_azure_resource_name_from_options()
    {
        var provider = Get("azure-openai");
        var credentials = new Dictionary<string, string> { ["apiKey"] = "azure-key" };
        var options = new Dictionary<string, string> { ["resourceName"] = "my-resource" };

        var client = Factory.Create(provider, "gpt-4o", credentials, options);

        client.ShouldNotBeNull();
    }

    // ── No-auth providers ─────────────────────────────────────────────────────

    [Fact]
    public void create_returns_chat_client_for_ollama_with_no_credentials()
    {
        var provider = Get("ollama");
        var credentials = new Dictionary<string, string>();

        var client = Factory.Create(provider, "llama3", credentials);

        client.ShouldNotBeNull();
    }

    // ── Non-OpenAI-compatible provider ────────────────────────────────────────

    [Fact]
    public void create_throws_for_non_openai_compatible_provider()
    {
        var nonCompatible = new ProviderDefinition
        {
            Id = "non-compat",
            DisplayName = "Non-Compatible",
            AuthMechanism = AuthMechanism.None,
            IsOpenAiCompatible = false,
        };

        Should.Throw<NotSupportedException>(() =>
            Factory.Create(nonCompatible, "some-model", new Dictionary<string, string>()));
    }
}
