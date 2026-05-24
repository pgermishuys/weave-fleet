using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuCode.Providers;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.NuCode;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.NuCode;

public sealed class NuCodeHarnessRuntimeTests
{
    private static NuCodeHarnessRuntime CreateRuntime(
        IUserPreferenceRepository prefs,
        INuCodeCredentialStore? credentialStore = null)
    {
        var registry = new ProviderRegistry(BuiltInProviders.All());
        var store = credentialStore ?? new InMemoryNuCodeCredentialStore();

        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddScoped<IUserPreferenceRepository>(_ => prefs);
            services.AddScoped<INuCodeCredentialStore>(_ => store);
            services.AddSingleton<IProviderRegistry>(_ => registry);
            services.AddSingleton<IChatClientFactory, NuCodeChatClientFactory>();
        });

        return new NuCodeHarnessRuntime(
            scopeFactory: scopeFactory,
            loggerFactory: NullLoggerFactory.Instance,
            logger: NullLogger<NuCodeHarnessRuntime>.Instance,
            modelDiscovery: new NoOpModelDiscoveryService());
    }

    // ── CheckAvailabilityAsync ────────────────────────────────────────────────

    [Fact]
    public async Task check_availability_returns_false_when_preference_missing()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        var runtime = CreateRuntime(prefs);

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeFalse();
        result.Reason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task check_availability_returns_false_when_preference_is_false()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        prefs.Seed("nucode.enabled", "false");
        var runtime = CreateRuntime(prefs);

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeFalse();
    }

    [Fact]
    public async Task check_availability_returns_true_when_preference_is_true()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        prefs.Seed("nucode.enabled", "true");
        var runtime = CreateRuntime(prefs);

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeTrue();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task check_availability_is_case_insensitive_for_true_value()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        prefs.Seed("nucode.enabled", "True");
        var runtime = CreateRuntime(prefs);

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeTrue();
    }

    // ── PrepareRuntimeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task prepare_runtime_uses_provider_and_model_from_preferences()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        prefs.Seed("nucode.enabled", "true");
        prefs.Seed("nucode.provider", "anthropic");
        prefs.Seed("nucode.modelId", "claude-opus-4-20250514");

        var store = new InMemoryNuCodeCredentialStore();
        await store.SetAsync("anthropic", "apiKey", "sk-test-key");

        var runtime = CreateRuntime(prefs, store);

        var context = new RuntimePreparationContext
        {
            UserId = "user-1",
            WorkingDirectory = "/tmp",
            UserCredentials = [],
        };

        var result = await runtime.PrepareRuntimeAsync(context, CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        var artifacts = ready.Artifacts.ShouldBeOfType<NuCodeLaunchArtifacts>();
        artifacts.ProviderId.ShouldBe("anthropic");
        artifacts.ModelId.ShouldBe("claude-opus-4-20250514");
        artifacts.Credentials["apiKey"].ShouldBe("sk-test-key");
    }

    [Fact]
    public async Task prepare_runtime_includes_base_url_from_preferences()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        prefs.Seed("nucode.enabled", "true");
        prefs.Seed("nucode.provider", "openai");
        prefs.Seed("nucode.modelId", "gpt-4o");
        prefs.Seed("nucode.baseUrl", "http://localhost:11434/v1");

        var store = new InMemoryNuCodeCredentialStore();
        await store.SetAsync("openai", "apiKey", "sk-openai");

        var runtime = CreateRuntime(prefs, store);

        var context = new RuntimePreparationContext
        {
            UserId = "user-1",
            WorkingDirectory = "/tmp",
            UserCredentials = [],
        };

        var result = await runtime.PrepareRuntimeAsync(context, CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        var artifacts = ready.Artifacts.ShouldBeOfType<NuCodeLaunchArtifacts>();
        artifacts.ProviderOptions.ShouldNotBeNull();
        artifacts.ProviderOptions["baseUrl"].ShouldBe("http://localhost:11434/v1");
    }

    [Fact]
    public async Task prepare_runtime_returns_not_ready_when_nucode_disabled()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        // nucode.enabled not set — disabled by default

        var runtime = CreateRuntime(prefs);

        var context = new RuntimePreparationContext
        {
            UserId = "user-1",
            WorkingDirectory = "/tmp",
            UserCredentials = [],
        };

        var result = await runtime.PrepareRuntimeAsync(context, CancellationToken.None);

        var notReady = result.ShouldBeOfType<RuntimePreparation.NotReady>();
        notReady.Errors.ShouldContain(e => e.Code == "NuCodeDisabled");
    }

    [Fact]
    public async Task prepare_runtime_returns_not_ready_when_credential_missing()
    {
        var prefs = new InMemoryUserPreferenceRepository();
        prefs.Seed("nucode.enabled", "true");
        prefs.Seed("nucode.provider", "anthropic");
        prefs.Seed("nucode.modelId", "claude-sonnet-4-20250514");

        var runtime = CreateRuntime(prefs); // no credentials stored

        var context = new RuntimePreparationContext
        {
            UserId = "user-1",
            WorkingDirectory = "/tmp",
            UserCredentials = [],
        };

        var result = await runtime.PrepareRuntimeAsync(context, CancellationToken.None);

        result.ShouldBeOfType<RuntimePreparation.NotReady>();
    }

    private sealed class NoOpModelDiscoveryService : IModelDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
            ProviderDefinition provider,
            IReadOnlyDictionary<string, string> credentials,
            IReadOnlyDictionary<string, string>? options = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscoveredModel>>([]);
    }
}
