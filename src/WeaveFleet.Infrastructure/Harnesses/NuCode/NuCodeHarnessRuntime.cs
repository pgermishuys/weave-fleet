using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using global::NuCode;
using global::NuCode.Tools;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

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
    private readonly IAnalyticsCollector? _analyticsCollector;

    public NuCodeHarnessRuntime(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<NuCodeHarnessRuntime> logger,
        IAnalyticsCollector? analyticsCollector = null)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _analyticsCollector = analyticsCollector;
    }

    /// <inheritdoc />
    public string HarnessType => "nucode";

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var prefs = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
        var enabled = await prefs.GetAsync(NuCodePreferenceKeys.Enabled).ConfigureAwait(false);

        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new HarnessAvailability(false,
                "NuCode is not enabled. Enable it in Settings → NuCode.");
        }

        return new HarnessAvailability(true, null);
    }

    /// <inheritdoc />
    public async Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
        // Read provider/model from preferences; fall back to inference from modelId
        using var scope = _scopeFactory.CreateScope();
        var prefs = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();

        var prefProvider = await prefs.GetAsync(NuCodePreferenceKeys.Provider).ConfigureAwait(false);
        var prefModelId = await prefs.GetAsync(NuCodePreferenceKeys.ModelId).ConfigureAwait(false);
        var prefBaseUrl = await prefs.GetAsync(NuCodePreferenceKeys.BaseUrl).ConfigureAwait(false);

        // Resolve provider: preference wins, then context modelId inference, then default
        var effectiveProvider = !string.IsNullOrWhiteSpace(prefProvider)
            ? prefProvider
            : (!string.IsNullOrEmpty(context.ModelId) ? InferProvider(context.ModelId) : "copilot");

        // Resolve modelId: preference wins, then context modelId
        var effectiveModelId = !string.IsNullOrWhiteSpace(prefModelId)
            ? prefModelId
            : (context.ModelId ?? "claude-sonnet-4-20250514");

        var requirements = ResolveRequirements(effectiveProvider);

        var errors = new List<RuntimePreparationError>();
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
                if (requirement.IsCopilot)
                    resolvedGitHubToken = match.EncryptedValue;
                else
                    resolvedApiKey = match.EncryptedValue;
            }
        }

        if (errors.Count > 0)
        {
            return new RuntimePreparation.NotReady(errors);
        }

        var artifacts = new NuCodeLaunchArtifacts(
            Provider: effectiveProvider,
            ModelId: effectiveModelId,
            ApiKey: resolvedApiKey ?? "",
            GitHubToken: resolvedGitHubToken,
            BaseUrl: string.IsNullOrWhiteSpace(prefBaseUrl) ? null : prefBaseUrl);

        return new RuntimePreparation.Ready(artifacts);
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

        // Override IQuestionService — questions are not supported in the orchestrator context.
        nuCodeServices.AddSingleton<IQuestionService, DenyAllQuestionService>();

        // Build the IChatClient from credentials
        var chatClient = ChatClientFactory.Create(
            artifacts.Provider, artifacts.ModelId, apiKeyOrToken, artifacts.BaseUrl);
        nuCodeServices.AddSingleton(chatClient);

        var nuCodeProvider = nuCodeServices.BuildServiceProvider();

        var instanceId = $"nucode-{Guid.NewGuid():N}";

        var session = new NuCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            provider: artifacts.Provider,
            modelId: artifacts.ModelId,
            projectId: options.ProjectId,
            projectName: options.ProjectName,
            ownerUserId: options.OwnerUserId,
            scopeFactory: _scopeFactory,
            nuCodeProvider: nuCodeProvider,
            chatClient: chatClient,
            logger: _loggerFactory.CreateLogger<NuCodeHarnessSession>(),
            analyticsCollector: _analyticsCollector);

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

    private static IReadOnlyList<CredentialRequirement> ResolveRequirements(string provider)
    {
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
            "custom" => [new CredentialRequirement(
                ProviderName: "custom",
                Namespace: "custom",
                Kind: "api-key",
                IsCopilot: false,
                UserFacingMessage: "An API key is required for the custom endpoint (leave empty for local models).",
                Guidance: "Add an API key in Settings → Credentials, or leave empty for local models like Ollama")],
            _ => [new CredentialRequirement(
                ProviderName: "copilot",
                Namespace: "github",
                Kind: "oauth-access-token",
                IsCopilot: true,
                UserFacingMessage: "A GitHub connection is required to use NuCode with Copilot.",
                Guidance: "Connect GitHub in Settings → Integrations → GitHub")]
        };
    }

    internal static string InferProvider(string modelId)
    {
        // Explicit prefix: "copilot/claude-sonnet-4-20250514"
        if (modelId.Contains('/'))
        {
            return modelId[..modelId.IndexOf('/', StringComparison.Ordinal)];
        }

        // Infer from model name
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return "anthropic";
        if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            return "openai";
        return "copilot";
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
