using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeHarnessPreparationTests
{
    [Fact]
    public async Task PrepareRuntimeAsync_WhenAnthropicModelMissingCredential_ReturnsNotReady()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext("anthropic/claude-3-7-sonnet"),
            CancellationToken.None);

        var notReady = result.ShouldBeOfType<RuntimePreparation.NotReady>();
        notReady.Errors.Count.ShouldBe(1);
        notReady.Errors[0].Code.ShouldBe("MissingCredential");
        notReady.Errors[0].Message.ShouldBe("An Anthropic API key is required to use this model.");
        notReady.Errors[0].Guidance.ShouldBe("Add an API key in Settings → Credentials");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenOpenAiModelMissingCredential_ReturnsNotReady()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext("openai/gpt-4.1"),
            CancellationToken.None);

        var notReady = result.ShouldBeOfType<RuntimePreparation.NotReady>();
        notReady.Errors.Count.ShouldBe(1);
        notReady.Errors[0].Code.ShouldBe("MissingCredential");
        notReady.Errors[0].Message.ShouldBe("An OpenAI API key is required to use this model.");
        notReady.Errors[0].Guidance.ShouldBe("Add an API key in Settings → Credentials");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenAnthropicCredentialPresent_ReturnsReadyWithAnthropicEnvironmentVariable()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext(
                "anthropic/claude-3-7-sonnet",
                CreateCredential("anthropic", "api-key", "anthropic-secret")),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        ready.Artifacts.GetType().Name.ShouldBe("OpenCodeLaunchArtifacts");
        var environmentVariables = GetEnvironmentVariables(ready.Artifacts);
        environmentVariables.Count.ShouldBe(1);
        environmentVariables["ANTHROPIC_API_KEY"].ShouldBe("anthropic-secret");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenOpenAiCredentialPresent_ReturnsReadyWithOpenAiEnvironmentVariable()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext(
                "openai/gpt-4.1",
                CreateCredential("openai", "api-key", "openai-secret")),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        ready.Artifacts.GetType().Name.ShouldBe("OpenCodeLaunchArtifacts");
        var environmentVariables = GetEnvironmentVariables(ready.Artifacts);
        environmentVariables.Count.ShouldBe(1);
        environmentVariables["OPENAI_API_KEY"].ShouldBe("openai-secret");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenModelIsUnknown_ReturnsReadyWithEmptyArtifacts()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext("custom/provider-model"),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        ready.Artifacts.GetType().Name.ShouldBe("OpenCodeLaunchArtifacts");
        GetEnvironmentVariables(ready.Artifacts).ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenModelIsNull_ReturnsReadyWithEmptyArtifacts()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContextWithNullModel(),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        ready.Artifacts.GetType().Name.ShouldBe("OpenCodeLaunchArtifacts");
        GetEnvironmentVariables(ready.Artifacts).ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareRuntimeAsync_MatchesNamespaceAndKindCaseInsensitively()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext(
                "anthropic/claude-3-7-sonnet",
                CreateCredential("AnThRoPiC", "API-KEY", "case-insensitive-secret")),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        var environmentVariables = GetEnvironmentVariables(ready.Artifacts);
        environmentVariables["ANTHROPIC_API_KEY"].ShouldBe("case-insensitive-secret");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenMultipleCredentialsMatch_UsesFirstCredentialInInputOrder()
    {
        var harness = CreateHarness();
        var firstCredential = CreateCredential("anthropic", "api-key", "first-secret");
        var secondCredential = CreateCredential("anthropic", "api-key", "second-secret");

        var result = await harness.PrepareRuntimeAsync(
            CreateContext("anthropic/claude-3-7-sonnet", firstCredential, secondCredential),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        var environmentVariables = GetEnvironmentVariables(ready.Artifacts);
        environmentVariables["ANTHROPIC_API_KEY"].ShouldBe("first-secret");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_names_the_built_in_skills_the_user_turned_on_that_Fleet_ships()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-run,retired-skill,fleet-code-review");
        var harness = CreateHarness(preferences);

        var result = await harness.PrepareRuntimeAsync(CreateContextWithNullModel(), CancellationToken.None);

        var environmentVariables = GetEnvironmentVariables(result.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts);
        environmentVariables[OpenCodeFleetSkills.BuiltInVariable].ShouldBe("fleet-code-review,fleet-run");
        environmentVariables.ContainsKey(WeaveFleet.Application.Walkthroughs.WalkthroughBridge.EnvironmentVariable).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareRuntimeAsync_gives_fleet_walkthrough_its_page_tool()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-walkthrough");
        var harness = CreateHarness(preferences);

        var result = await harness.PrepareRuntimeAsync(CreateContextWithNullModel(), CancellationToken.None);

        var environmentVariables = GetEnvironmentVariables(result.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts);
        environmentVariables[WeaveFleet.Application.Walkthroughs.WalkthroughBridge.EnvironmentVariable].ShouldBe("1");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_leaves_the_built_in_skills_out_until_the_user_turns_one_on()
    {
        var harness = CreateHarness(new InMemoryUserPreferenceRepository());

        var result = await harness.PrepareRuntimeAsync(CreateContextWithNullModel(), CancellationToken.None);

        GetEnvironmentVariables(result.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).ShouldBeEmpty();
    }

    [Fact]
    public void GetSkillFolders_adds_a_folder_for_each_built_in_skill_the_session_names()
    {
        using var data = new TempDirectory();
        var harness = CreateHarness(new InMemoryUserPreferenceRepository(), new FleetOptions { DatabasePath = Path.Combine(data.Path, "fleet.db") });

        var folders = harness.GetSkillFolders(new Dictionary<string, string>
        {
            [OpenCodeFleetSkills.BuiltInVariable] = "fleet-run,retired-skill",
        });

        folders.ShouldBe([
            Path.Combine(data.Path, "opencode", "skills"),
            Path.Combine(data.Path, "opencode", "built-in-skills", "fleet-run"),
        ]);
        File.Exists(Path.Combine(folders[1], "SKILL.md")).ShouldBeTrue();
    }

    [Fact]
    public void GetSkillFolders_offers_built_in_skills_with_auth_on_but_not_the_Fleet_API_skill()
    {
        using var data = new TempDirectory();
        var options = new FleetOptions { DatabasePath = Path.Combine(data.Path, "fleet.db") };
        options.Auth.Enabled = true;
        var harness = CreateHarness(new InMemoryUserPreferenceRepository(), options);

        harness.GetSkillFolders(new Dictionary<string, string>()).ShouldBeEmpty();
        harness.GetSkillFolders(new Dictionary<string, string> { [OpenCodeFleetSkills.BuiltInVariable] = "fleet-mockups" })
            .ShouldBe([Path.Combine(data.Path, "opencode", "built-in-skills", "fleet-mockups")]);
    }

    [Fact]
    public async Task PrepareRuntimeAsync_loads_the_users_own_version_of_a_skill_in_place_of_Fleets_copy()
    {
        using var data = new TempDirectory();
        var preferences = new InMemoryUserPreferenceRepository();
        preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review,fleet-run");
        using var store = new WeaveFleet.Infrastructure.Skills.FileSkillVersionStore(data.Path);
        await store.AddAsync(
            "user-1", "fleet-code-review", "---\nname: fleet-code-review\ndescription: Mine.\n---\n\nMy way.\n",
            new SkillVersionSource(null, null, null), OpenCodeFleetSkills.ContentOf("fleet-code-review")!);
        var harness = CreateHarness(preferences, store: store);

        var result = await harness.PrepareRuntimeAsync(CreateContextWithNullModel(), CancellationToken.None);

        var environmentVariables = GetEnvironmentVariables(result.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts);
        environmentVariables[OpenCodeFleetSkills.BuiltInVariable].ShouldBe("fleet-run");
        environmentVariables[OpenCodeFleetSkills.YourVersionsVariable].ShouldBe(store.FolderFor("user-1", "fleet-code-review", 1));
    }

    [Fact]
    public async Task PrepareRuntimeAsync_leaves_the_users_version_out_while_the_skill_is_off()
    {
        using var data = new TempDirectory();
        var preferences = new InMemoryUserPreferenceRepository();
        using var store = new WeaveFleet.Infrastructure.Skills.FileSkillVersionStore(data.Path);
        await store.AddAsync(
            "user-1", "fleet-code-review", "---\nname: fleet-code-review\ndescription: Mine.\n---\n",
            new SkillVersionSource(null, null, null), OpenCodeFleetSkills.ContentOf("fleet-code-review")!);
        var harness = CreateHarness(preferences, store: store);

        var result = await harness.PrepareRuntimeAsync(CreateContextWithNullModel(), CancellationToken.None);

        GetEnvironmentVariables(result.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).ShouldBeEmpty();
    }

    [Fact]
    public void GetSkillFolders_adds_the_folder_of_each_of_the_users_versions_that_exists()
    {
        using var data = new TempDirectory();
        var harness = CreateHarness(new InMemoryUserPreferenceRepository(), new FleetOptions { DatabasePath = Path.Combine(data.Path, "fleet.db") });
        var version = Path.Combine(data.Path, "skills", "v1");
        Directory.CreateDirectory(version);

        var folders = harness.GetSkillFolders(new Dictionary<string, string>
        {
            [OpenCodeFleetSkills.YourVersionsVariable] = string.Join(Path.PathSeparator, version, Path.Combine(data.Path, "gone")),
        });

        folders.ShouldBe([Path.Combine(data.Path, "opencode", "skills"), version]);
    }

    private static OpenCodeHarnessRuntime CreateHarness(
        IUserPreferenceRepository preferences, FleetOptions? options = null, ISkillVersionStore? store = null)
    {
        return new OpenCodeHarnessRuntime(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: options ?? new FleetOptions(),
            scopeFactory: TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton(preferences);
                if (store is not null)
                    services.AddSingleton(store);
            }),
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-skill-folders-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static OpenCodeHarnessRuntime CreateHarness()
    {
        return new OpenCodeHarnessRuntime(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: new FleetOptions(),
            scopeFactory: TestServiceScopeFactory.CreateEmpty(),
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);
    }

    private static RuntimePreparationContext CreateContext(string modelId)
    {
        return new RuntimePreparationContext
        {
            UserId = "user-1",
            UserCredentials = [],
            ModelId = modelId,
            WorkingDirectory = Path.GetTempPath()
        };
    }

    private static RuntimePreparationContext CreateContext(string modelId, UserCredential credential)
    {
        return CreateContext(modelId, [credential]);
    }

    private static RuntimePreparationContext CreateContext(string modelId, params UserCredential[] credentials)
    {
        return new RuntimePreparationContext
        {
            UserId = "user-1",
            UserCredentials = credentials,
            ModelId = modelId,
            WorkingDirectory = Path.GetTempPath()
        };
    }

    private static RuntimePreparationContext CreateContextWithNullModel()
    {
        return new RuntimePreparationContext
        {
            UserId = "user-1",
            UserCredentials = [],
            ModelId = null,
            WorkingDirectory = Path.GetTempPath()
        };
    }

    private static UserCredential CreateCredential(string credentialNamespace, string kind, string decryptedValue)
    {
        var timestamp = DateTime.UtcNow.ToString("O");
        return new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-1",
            Namespace = credentialNamespace,
            Kind = kind,
            Label = $"{credentialNamespace}-{kind}-{Guid.NewGuid():N}",
            EncryptedValue = decryptedValue,
            DisplayHint = decryptedValue.Length >= 4 ? decryptedValue[^4..] : decryptedValue,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    private static IReadOnlyDictionary<string, string> GetEnvironmentVariables(RuntimeLaunchArtifacts artifacts)
    {
        var property = artifacts.GetType().GetProperty(
            "EnvironmentVariables",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        property.ShouldNotBeNull();

        var value = property.GetValue(artifacts);
        value.ShouldNotBeNull();
        return (IReadOnlyDictionary<string, string>)value;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }
}
