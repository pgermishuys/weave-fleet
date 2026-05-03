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
public sealed partial class NuCodeHarnessRuntime : IHarnessRuntime
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NuCodeHarnessRuntime> _logger;

    public NuCodeHarnessRuntime(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<NuCodeHarnessRuntime> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
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
        string? resolvedGitHubToken = null;

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
                    Guidance: requirement.Guidance));
            }
            else
            {
                resolvedProvider = requirement.ProviderName;
                if (requirement.IsCopilot)
                {
                    resolvedGitHubToken = match.EncryptedValue;
                }
                else
                {
                    resolvedApiKey = match.EncryptedValue;
                }
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.NotReady(errors));
        }

        var modelId = context.ModelId ?? "claude-sonnet-4-20250514";
        var provider = resolvedProvider ?? "anthropic";

        var artifacts = new NuCodeLaunchArtifacts(
            Provider: provider,
            ModelId: modelId,
            ApiKey: resolvedApiKey ?? "",
            GitHubToken: resolvedGitHubToken);

        return Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts));
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var artifacts = options.LaunchArtifacts as NuCodeLaunchArtifacts
            ?? new NuCodeLaunchArtifacts("anthropic", "claude-sonnet-4-20250514", "");

        // For Copilot, exchange GitHub token for a short-lived Copilot API token
        var apiKeyOrToken = artifacts.ApiKey;
        if (string.Equals(artifacts.Provider, "copilot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(artifacts.GitHubToken))
        {
            var copilotToken = await CopilotTokenService.ExchangeAsync(
                _httpClientFactory, artifacts.GitHubToken, ct).ConfigureAwait(false);
            apiKeyOrToken = copilotToken.Token;
            LogCopilotTokenExchanged(artifacts.ModelId);
        }

        // Build NuCode services
        var nuCodeServices = new ServiceCollection();
        nuCodeServices.AddNuCode(nuCodeOptions =>
        {
            nuCodeOptions.WorkingDirectory = options.WorkingDirectory;
        });

        // Build the IChatClient from credentials
        var chatClient = ChatClientFactory.Create(artifacts.Provider, artifacts.ModelId, apiKeyOrToken);
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

        return session;
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
        var provider = string.IsNullOrEmpty(modelId) ? "copilot" : InferProvider(modelId);

        return provider.ToLowerInvariant() switch
        {
            "copilot" => [new CredentialRequirement(
                ProviderName: "copilot",
                Namespace: "github",
                Kind: "oauth-access-token",
                IsCopilot: true,
                UserFacingMessage: "A GitHub connection is required to use NuCode with Copilot.",
                Guidance: "Connect GitHub in Settings → Integrations → GitHub")],
            "anthropic" => [new CredentialRequirement(
                ProviderName: "anthropic",
                Namespace: "anthropic",
                Kind: "api-key",
                IsCopilot: false,
                UserFacingMessage: "An Anthropic API key is required to use this model.",
                Guidance: "Add an API key in Settings → Credentials")],
            "openai" => [new CredentialRequirement(
                ProviderName: "openai",
                Namespace: "openai",
                Kind: "api-key",
                IsCopilot: false,
                UserFacingMessage: "An OpenAI API key is required to use this model.",
                Guidance: "Add an API key in Settings → Credentials")],
            _ => [new CredentialRequirement(
                ProviderName: "copilot",
                Namespace: "github",
                Kind: "oauth-access-token",
                IsCopilot: true,
                UserFacingMessage: "A GitHub connection is required to use NuCode with Copilot.",
                Guidance: "Connect GitHub in Settings → Integrations → GitHub")]
        };
    }

    private static string InferProvider(string modelId)
    {
        // Explicit prefix: "copilot/claude-sonnet-4-20250514"
        if (modelId.Contains('/'))
        {
            return modelId[..modelId.IndexOf('/', StringComparison.Ordinal)];
        }

        // Infer from model name
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return "copilot"; // Default Claude models through Copilot
        if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            return "copilot"; // GPT/o-series also available via Copilot
        return "copilot"; // Default to Copilot
    }

    private sealed record CredentialRequirement(
        string ProviderName,
        string Namespace,
        string Kind,
        bool IsCopilot,
        string UserFacingMessage,
        string Guidance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exchanged GitHub token for Copilot API token (model: {ModelId})")]
    private partial void LogCopilotTokenExchanged(string modelId);
}
