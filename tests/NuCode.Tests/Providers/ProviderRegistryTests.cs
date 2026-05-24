using NuCode.Providers;

namespace NuCode.Tests.Providers;

public sealed class ProviderRegistryTests
{
    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public void get_by_id_returns_provider_when_found()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.GetById("anthropic");

        result.ShouldNotBeNull();
        result.Id.ShouldBe("anthropic");
    }

    [Fact]
    public void get_by_id_is_case_insensitive()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.GetById("ANTHROPIC");

        result.ShouldNotBeNull();
        result.Id.ShouldBe("anthropic");
    }

    [Fact]
    public void get_by_id_returns_null_for_unknown_provider()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.GetById("does-not-exist");

        result.ShouldBeNull();
    }

    // ── GetAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void get_all_returns_all_built_in_providers()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var all = registry.GetAll();

        all.ShouldNotBeEmpty();
        all.Count.ShouldBeGreaterThanOrEqualTo(4); // at minimum: anthropic, openai, copilot, custom
    }

    [Fact]
    public void get_all_includes_core_providers()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var ids = registry.GetAll().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ids.ShouldContain("anthropic");
        ids.ShouldContain("openai");
        ids.ShouldContain("copilot");
    }

    // ── InferFromModelId ──────────────────────────────────────────────────────

    [Fact]
    public void infer_from_model_id_uses_slash_notation()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.InferFromModelId("copilot/claude-sonnet-4-20250514");

        result.ShouldBe("copilot");
    }

    [Fact]
    public void infer_from_model_id_uses_slash_notation_for_openai()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.InferFromModelId("openai/gpt-4o");

        result.ShouldBe("openai");
    }

    [Fact]
    public void infer_from_model_id_matches_claude_prefix_to_anthropic()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.InferFromModelId("claude-opus-4-20250514");

        result.ShouldBe("anthropic");
    }

    [Fact]
    public void infer_from_model_id_matches_gpt_prefix_to_openai()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.InferFromModelId("gpt-4o");

        result.ShouldBe("openai");
    }

    [Fact]
    public void infer_from_model_id_falls_back_to_copilot_for_unknown_model()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.InferFromModelId("totally-unknown-model-xyz");

        result.ShouldBe("copilot");
    }

    [Fact]
    public void infer_from_model_id_falls_back_to_copilot_for_empty_string()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        var result = registry.InferFromModelId(string.Empty);

        result.ShouldBe("copilot");
    }

    [Fact]
    public void infer_from_model_id_slash_notation_ignores_unknown_prefix()
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());

        // "unknown-provider/gpt-4o" — slash prefix not in registry, falls through to prefix matching
        var result = registry.InferFromModelId("unknown-provider/gpt-4o");

        // "gpt-4o" doesn't match as a whole model ID prefix, but the slash prefix is unknown
        // so it falls back to prefix matching on the full string — "unknown-provider" won't match
        result.ShouldBe("copilot");
    }

    // ── Register ──────────────────────────────────────────────────────────────

    [Fact]
    public void register_adds_new_provider()
    {
        var registry = new ProviderRegistry([]);

        var custom = new ProviderDefinition
        {
            Id = "my-custom",
            DisplayName = "My Custom Provider",
            AuthMechanism = AuthMechanism.ApiKey,
            CredentialFields = [new CredentialField("apiKey", "API Key", Required: true, IsSecret: true, HelpText: null)],
        };

        registry.Register(custom);

        registry.GetById("my-custom").ShouldNotBeNull();
    }

    [Fact]
    public void register_overrides_existing_provider()
    {
        var original = new ProviderDefinition
        {
            Id = "test-provider",
            DisplayName = "Original",
            AuthMechanism = AuthMechanism.None,
        };

        var registry = new ProviderRegistry([original]);

        var updated = original with { DisplayName = "Updated" };
        registry.Register(updated);

        registry.GetById("test-provider")!.DisplayName.ShouldBe("Updated");
    }

    [Fact]
    public void register_custom_provider_is_returned_by_get_all()
    {
        var registry = new ProviderRegistry([]);

        var custom = new ProviderDefinition
        {
            Id = "custom-llm",
            DisplayName = "Custom LLM",
            AuthMechanism = AuthMechanism.None,
            CredentialOptional = true,
        };

        registry.Register(custom);

        registry.GetAll().ShouldContain(p => p.Id == "custom-llm");
    }

    // ── Built-in catalog completeness ─────────────────────────────────────────

    [Fact]
    public void built_in_providers_have_no_duplicate_ids()
    {
        var all = BuiltInProviders.All();
        var ids = all.Select(p => p.Id).ToList();
        var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        distinct.Count.ShouldBe(ids.Count);
    }

    [Fact]
    public void built_in_providers_all_have_non_empty_display_names()
    {
        foreach (var provider in BuiltInProviders.All())
        {
            provider.DisplayName.ShouldNotBeNullOrWhiteSpace(
                $"Provider '{provider.Id}' has an empty DisplayName");
        }
    }

    [Fact]
    public void built_in_api_key_providers_have_at_least_one_secret_credential_field()
    {
        var apiKeyProviders = BuiltInProviders.All()
            .Where(p => p.AuthMechanism == AuthMechanism.ApiKey);

        foreach (var provider in apiKeyProviders)
        {
            provider.CredentialFields.ShouldContain(
                f => f.IsSecret,
                $"API-key provider '{provider.Id}' has no secret credential field");
        }
    }
}
