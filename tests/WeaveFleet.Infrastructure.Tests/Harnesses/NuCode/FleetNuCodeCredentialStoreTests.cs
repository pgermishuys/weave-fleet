using NuCode.Providers;
using WeaveFleet.Infrastructure.Harnesses.NuCode;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.NuCode;

public sealed class FleetNuCodeCredentialStoreTests
{
    private static FleetNuCodeCredentialStore CreateStore(
        InMemoryUserCredentialRepository? repo = null,
        string userId = TestUserContext.DefaultUserId)
    {
        repo ??= new InMemoryUserCredentialRepository();
        var protector = new FakeCredentialProtector();
        var userContext = new TestUserContext(userId);
        return new FleetNuCodeCredentialStore(repo, protector, userContext);
    }

    // ── SetAsync / GetAsync round-trip ────────────────────────────────────────

    [Fact]
    public async Task set_and_get_round_trips_credential_value()
    {
        var store = CreateStore();

        await store.SetAsync("anthropic", "apiKey", "sk-ant-test");
        var result = await store.GetAsync("anthropic", "apiKey");

        result.ShouldNotBeNull();
        result.Value.ShouldBe("sk-ant-test");
        result.ProviderId.ShouldBe("anthropic");
        result.FieldKey.ShouldBe("apiKey");
    }

    [Fact]
    public async Task get_returns_null_when_credential_not_stored()
    {
        var store = CreateStore();

        var result = await store.GetAsync("anthropic", "apiKey");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task set_overwrites_existing_credential()
    {
        var store = CreateStore();

        await store.SetAsync("openai", "apiKey", "sk-first");
        await store.SetAsync("openai", "apiKey", "sk-second");

        var result = await store.GetAsync("openai", "apiKey");
        result!.Value.ShouldBe("sk-second");
    }

    // ── Namespace mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task set_stores_credential_with_nucode_namespace_prefix()
    {
        var repo = new InMemoryUserCredentialRepository();
        var store = CreateStore(repo);

        await store.SetAsync("anthropic", "apiKey", "sk-test");

        var stored = repo.All.ShouldHaveSingleItem();
        stored.Namespace.ShouldBe("nucode:anthropic");
    }

    [Fact]
    public async Task set_stores_credential_with_field_key_as_kind()
    {
        var repo = new InMemoryUserCredentialRepository();
        var store = CreateStore(repo);

        await store.SetAsync("anthropic", "apiKey", "sk-test");

        var stored = repo.All.ShouldHaveSingleItem();
        stored.Kind.ShouldBe("apiKey");
    }

    [Fact]
    public async Task set_stores_credential_with_correct_label()
    {
        var repo = new InMemoryUserCredentialRepository();
        var store = CreateStore(repo);

        await store.SetAsync("anthropic", "apiKey", "sk-test");

        var stored = repo.All.ShouldHaveSingleItem();
        stored.Label.ShouldBe("nucode:anthropic:apikey");
    }

    // ── Expiry ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task set_with_expiry_round_trips_expires_at()
    {
        var store = CreateStore();
        var expiry = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await store.SetAsync("copilot", "copilotToken", "ghu_token", expiresAt: expiry);
        var result = await store.GetAsync("copilot", "copilotToken");

        result.ShouldNotBeNull();
        result.ExpiresAt.ShouldBe(expiry);
    }

    [Fact]
    public async Task set_without_expiry_returns_null_expires_at()
    {
        var store = CreateStore();

        await store.SetAsync("anthropic", "apiKey", "sk-test");
        var result = await store.GetAsync("anthropic", "apiKey");

        result.ShouldNotBeNull();
        result.ExpiresAt.ShouldBeNull();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task delete_removes_specific_credential_field()
    {
        var store = CreateStore();
        await store.SetAsync("anthropic", "apiKey", "sk-test");

        await store.DeleteAsync("anthropic", "apiKey");

        var result = await store.GetAsync("anthropic", "apiKey");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task delete_is_noop_when_credential_not_stored()
    {
        var store = CreateStore();

        // Should not throw
        await store.DeleteAsync("anthropic", "apiKey");
    }

    [Fact]
    public async Task delete_only_removes_specified_field()
    {
        var store = CreateStore();
        await store.SetAsync("azure-openai", "apiKey", "key1");
        await store.SetAsync("azure-openai", "resourceName", "my-resource");

        await store.DeleteAsync("azure-openai", "apiKey");

        (await store.GetAsync("azure-openai", "apiKey")).ShouldBeNull();
        (await store.GetAsync("azure-openai", "resourceName")).ShouldNotBeNull();
    }

    // ── DeleteAllForProviderAsync ─────────────────────────────────────────────

    [Fact]
    public async Task delete_all_removes_all_fields_for_provider()
    {
        var store = CreateStore();
        await store.SetAsync("azure-openai", "apiKey", "key1");
        await store.SetAsync("azure-openai", "resourceName", "my-resource");

        await store.DeleteAllForProviderAsync("azure-openai");

        var remaining = await store.GetAllForProviderAsync("azure-openai");
        remaining.ShouldBeEmpty();
    }

    [Fact]
    public async Task delete_all_does_not_affect_other_providers()
    {
        var store = CreateStore();
        await store.SetAsync("anthropic", "apiKey", "sk-ant");
        await store.SetAsync("openai", "apiKey", "sk-oai");

        await store.DeleteAllForProviderAsync("anthropic");

        (await store.GetAsync("openai", "apiKey")).ShouldNotBeNull();
    }

    // ── GetAllForProviderAsync ────────────────────────────────────────────────

    [Fact]
    public async Task get_all_for_provider_returns_all_stored_fields()
    {
        var store = CreateStore();
        await store.SetAsync("azure-openai", "apiKey", "key1");
        await store.SetAsync("azure-openai", "resourceName", "my-resource");

        var results = await store.GetAllForProviderAsync("azure-openai");

        results.Count.ShouldBe(2);
        results.ShouldContain(c => c.FieldKey == "apiKey");
        results.ShouldContain(c => c.FieldKey == "resourceName");
    }

    [Fact]
    public async Task get_all_for_provider_returns_empty_when_none_stored()
    {
        var store = CreateStore();

        var results = await store.GetAllForProviderAsync("anthropic");

        results.ShouldBeEmpty();
    }

    // ── ListConfiguredProviderIdsAsync ────────────────────────────────────────

    [Fact]
    public async Task list_configured_provider_ids_returns_providers_with_credentials()
    {
        var store = CreateStore();
        await store.SetAsync("anthropic", "apiKey", "sk-ant");
        await store.SetAsync("openai", "apiKey", "sk-oai");

        var ids = await store.ListConfiguredProviderIdsAsync();

        ids.ShouldContain("anthropic");
        ids.ShouldContain("openai");
    }

    [Fact]
    public async Task list_configured_provider_ids_returns_empty_when_none_stored()
    {
        var store = CreateStore();

        var ids = await store.ListConfiguredProviderIdsAsync();

        ids.ShouldBeEmpty();
    }

    [Fact]
    public async Task list_configured_provider_ids_deduplicates_multiple_fields()
    {
        var store = CreateStore();
        await store.SetAsync("azure-openai", "apiKey", "key1");
        await store.SetAsync("azure-openai", "resourceName", "my-resource");

        var ids = await store.ListConfiguredProviderIdsAsync();

        ids.Count(id => string.Equals(id, "azure-openai", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1);
    }
}
