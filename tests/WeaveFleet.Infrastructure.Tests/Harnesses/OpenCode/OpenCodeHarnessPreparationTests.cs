using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Testing.Fakes;

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
