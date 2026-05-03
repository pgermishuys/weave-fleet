using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using global::NuCode;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// <see cref="IHarnessRuntime"/> implementation for the NuCode in-process AI coding agent.
/// Handles availability checks, credential resolution, and spawning/resuming sessions.
/// </summary>
public sealed class NuCodeHarnessRuntime : IHarnessRuntime
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NuCodeHarnessRuntime> _logger;

    public NuCodeHarnessRuntime(
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        ILogger<NuCodeHarnessRuntime> logger)
    {
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string HarnessType => "nucode";

    /// <inheritdoc />
    public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        // NuCode is in-process — always available.
        return Task.FromResult(new HarnessAvailability(true, null));
    }

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
        var requirements = ResolveRequirements(context.ModelId);

        var errors = new List<RuntimePreparationError>();
        string? resolvedProvider = null;
        string? resolvedApiKey = null;

        foreach (var requirement in requirements)
        {
            var match = context.UserCredentials
                .FirstOrDefault(c =>
                    string.Equals(c.Namespace, requirement.Namespace, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Kind, requirement.Kind, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                errors.Add(new RuntimePreparationError(
                    Code: "MissingCredential",
                    Message: requirement.UserFacingMessage,
                    Guidance: "Add an API key in Settings → Credentials"));
            }
            else
            {
                resolvedProvider = requirement.Namespace;
                resolvedApiKey = match.EncryptedValue;
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.NotReady(errors));
        }

        var modelId = context.ModelId ?? "claude-sonnet-4-20250514";
        var provider = resolvedProvider ?? "anthropic";

        return Task.FromResult<RuntimePreparation>(
            new RuntimePreparation.Ready(new NuCodeLaunchArtifacts(provider, modelId, resolvedApiKey ?? "")));
    }

    /// <inheritdoc />
    public Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var artifacts = options.LaunchArtifacts as NuCodeLaunchArtifacts
            ?? new NuCodeLaunchArtifacts("anthropic", "claude-sonnet-4-20250514", "");

        // Create a DI scope for this NuCode session
        var scope = _scopeFactory.CreateScope();

        // Build NuCode services within the scope
        var nuCodeServices = new ServiceCollection();
        nuCodeServices.AddNuCode(nuCodeOptions =>
        {
            nuCodeOptions.WorkingDirectory = options.WorkingDirectory;
        });

        // Build the IChatClient from credentials
        var chatClient = ChatClientFactory.Create(artifacts.Provider, artifacts.ModelId, artifacts.ApiKey);
        nuCodeServices.AddSingleton(chatClient);

        var nuCodeProvider = nuCodeServices.BuildServiceProvider();

        var instanceId = $"nucode-{Guid.NewGuid():N}";
        var session = new NuCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            nuCodeProvider: nuCodeProvider,
            chatClient: chatClient,
            logger: _loggerFactory.CreateLogger<NuCodeHarnessSession>());

        if (options.InitialPrompt is not null)
        {
            // Fire-and-forget the initial prompt processing
            _ = session.SendPromptAsync(options.InitialPrompt, null, ct);
        }

        return Task.FromResult<IHarnessSession>(session);
    }

    /// <inheritdoc />
    public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        // For now, resume creates a fresh session (NuCode sessions are ephemeral in-memory by default).
        // TODO: Implement proper resume using NuCode's SQLite session store.
        var spawnOptions = new HarnessSpawnOptions
        {
            SessionId = options.SessionId,
            WorkingDirectory = options.WorkingDirectory,
            OwnerUserId = options.OwnerUserId,
            ProjectId = options.ProjectId,
            ProjectName = options.ProjectName,
            LaunchArtifacts = options.LaunchArtifacts,
        };

        return SpawnAsync(spawnOptions, ct);
    }

    private static IReadOnlyList<CredentialRequirement> ResolveRequirements(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            // Default to Anthropic
            return [new CredentialRequirement(
                Namespace: "anthropic",
                Kind: "api-key",
                UserFacingMessage: "An Anthropic API key is required to use NuCode.")];
        }

        var provider = modelId.Contains('/')
            ? modelId[..modelId.IndexOf('/')]
            : InferProvider(modelId);

        return provider.ToLowerInvariant() switch
        {
            "anthropic" => [new CredentialRequirement(
                Namespace: "anthropic",
                Kind: "api-key",
                UserFacingMessage: "An Anthropic API key is required to use this model.")],
            "openai" => [new CredentialRequirement(
                Namespace: "openai",
                Kind: "api-key",
                UserFacingMessage: "An OpenAI API key is required to use this model.")],
            _ => []
        };
    }

    private static string InferProvider(string modelId)
    {
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return "anthropic";
        if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase))
            return "openai";
        return "anthropic"; // Default
    }

    private sealed record CredentialRequirement(
        string Namespace,
        string Kind,
        string UserFacingMessage);
}
