using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeHarnessPreparationTests
{
    [Fact]
    public async Task PrepareRuntimeAsync_WhenCredentialBagIsEmpty_ReturnsReady()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext([], null),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        ready.Artifacts.GetType().Name.ShouldBe("ClaudeCodeLaunchArtifacts");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_WhenCredentialBagIsPopulated_ReturnsReady()
    {
        var harness = CreateHarness();

        var result = await harness.PrepareRuntimeAsync(
            CreateContext([CreateCredential("anthropic", "api-key", "secret-1")], "anthropic/claude-3-7-sonnet"),
            CancellationToken.None);

        var ready = result.ShouldBeOfType<RuntimePreparation.Ready>();
        ready.Artifacts.GetType().Name.ShouldBe("ClaudeCodeLaunchArtifacts");
    }

    [Fact]
    public async Task PrepareRuntimeAsync_IgnoresModelChoiceAndAlwaysReturnsNoOpArtifacts()
    {
        var harness = CreateHarness();

        var firstResult = await harness.PrepareRuntimeAsync(
            CreateContext([], "openai/gpt-4.1"),
            CancellationToken.None);

        var secondResult = await harness.PrepareRuntimeAsync(
            CreateContext([CreateCredential("openai", "api-key", "secret-2")], "custom/provider-model"),
            CancellationToken.None);

        firstResult.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts.GetType().Name.ShouldBe("ClaudeCodeLaunchArtifacts");
        secondResult.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts.GetType().Name.ShouldBe("ClaudeCodeLaunchArtifacts");
    }

    private static ClaudeCodeHarnessRuntime CreateHarness()
    {
        return new ClaudeCodeHarnessRuntime(
            options: new FleetOptions(),
            scopeFactory: TestServiceScopeFactory.CreateEmpty(),
            logger: NullLogger<ClaudeCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);
    }

    private static RuntimePreparationContext CreateContext(IReadOnlyList<UserCredential> credentials, string? modelId)
    {
        return new RuntimePreparationContext
        {
            UserId = "user-1",
            UserCredentials = credentials,
            ModelId = modelId,
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
}
