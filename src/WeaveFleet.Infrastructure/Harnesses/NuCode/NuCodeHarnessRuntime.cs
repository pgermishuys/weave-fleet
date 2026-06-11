using global::NuCode;
using global::NuCode.Agents;
using global::NuCode.Providers;
using global::NuCode.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NuCodeHarnessRuntime> _logger;
    private readonly IModelDiscoveryService _modelDiscovery;
    private readonly IAnalyticsCollector? _analyticsCollector;

    public NuCodeHarnessRuntime(
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        ILogger<NuCodeHarnessRuntime> logger,
        IModelDiscoveryService modelDiscovery,
        IAnalyticsCollector? analyticsCollector = null)
    {
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _modelDiscovery = modelDiscovery;
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
        using var scope = _scopeFactory.CreateScope();
        var prefs = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
        var credentialStore = scope.ServiceProvider.GetRequiredService<INuCodeCredentialStore>();
        var registry = scope.ServiceProvider.GetRequiredService<IProviderRegistry>();

        // Guard: NuCode must be explicitly enabled
        var enabled = await prefs.GetAsync(NuCodePreferenceKeys.Enabled).ConfigureAwait(false);
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimePreparation.NotReady(
            [
                new RuntimePreparationError(
                    Code: "NuCodeDisabled",
                    Message: "NuCode is not enabled.",
                    Guidance: "Enable it in Settings → NuCode.")
            ]);
        }

        var prefProvider = await prefs.GetAsync(NuCodePreferenceKeys.Provider).ConfigureAwait(false);
        var prefModelId = await prefs.GetAsync(NuCodePreferenceKeys.ModelId).ConfigureAwait(false);
        var prefBaseUrl = await prefs.GetAsync(NuCodePreferenceKeys.BaseUrl).ConfigureAwait(false);

        // Resolve provider: preference wins, then context modelId inference, then default
        var effectiveProviderId = !string.IsNullOrWhiteSpace(prefProvider)
            ? prefProvider
            : (!string.IsNullOrEmpty(context.ModelId)
                ? registry.InferFromModelId(context.ModelId)
                : "copilot");

        // Resolve modelId: preference wins, then context modelId
        var effectiveModelId = !string.IsNullOrWhiteSpace(prefModelId)
            ? prefModelId
            : (context.ModelId ?? "gpt-4o");

        var provider = registry.GetById(effectiveProviderId);
        if (provider is null)
        {
            return new RuntimePreparation.NotReady(
            [
                new RuntimePreparationError(
                    Code: "UnknownProvider",
                    Message: $"Provider '{effectiveProviderId}' is not recognised.",
                    Guidance: "Select a supported provider in Settings → NuCode.")
            ]);
        }

        // Resolve credentials from NuCode's own credential store
        var errors = new List<RuntimePreparationError>();
        var resolvedCredentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in provider.CredentialFields)
        {
            if (!field.IsSecret)
                continue; // Non-secret fields come from provider options, not credential store

            var stored = await credentialStore.GetAsync(provider.Id, field.Key, ct).ConfigureAwait(false);
            if (stored is null && field.Required && !provider.CredentialOptional)
            {
                errors.Add(new RuntimePreparationError(
                    Code: "MissingCredential",
                    Message: $"A credential is required for provider '{provider.DisplayName}' (field: {field.DisplayName}).",
                    Guidance: $"Add credentials in Settings → Providers → {provider.DisplayName}."));
            }
            else if (stored is not null)
            {
                resolvedCredentials[field.Key] = stored.Value;
            }
        }

        if (errors.Count > 0)
            return new RuntimePreparation.NotReady(errors);

        // Build provider options (baseUrl override, etc.)
        var providerOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(prefBaseUrl))
            providerOptions["baseUrl"] = prefBaseUrl;

        var artifacts = new NuCodeLaunchArtifacts(
            ProviderId: effectiveProviderId,
            ModelId: effectiveModelId,
            Credentials: resolvedCredentials,
            ProviderOptions: providerOptions.Count > 0 ? providerOptions : null);

        return new RuntimePreparation.Ready(artifacts);
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var artifacts = options.LaunchArtifacts as NuCodeLaunchArtifacts
            ?? new NuCodeLaunchArtifacts(
                "copilot",
                "gpt-4o",
                new Dictionary<string, string>());

        using var scope = _scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IProviderRegistry>();
        var chatClientFactory = scope.ServiceProvider.GetRequiredService<IChatClientFactory>();

        var provider = registry.GetById(artifacts.ProviderId)
            ?? throw new InvalidOperationException($"Unknown provider '{artifacts.ProviderId}'.");

        // Build NuCode services
        var nuCodeServices = new ServiceCollection();
        nuCodeServices.AddSingleton(_loggerFactory);
        nuCodeServices.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        nuCodeServices.AddNuCode(nuCodeOptions =>
        {
            nuCodeOptions.WorkingDirectory = options.WorkingDirectory;
        });

        // Override IQuestionService — questions are not supported in the orchestrator context.
        nuCodeServices.AddSingleton<IQuestionService, DenyAllQuestionService>();

        // Build the IChatClient from credentials
        var chatClient = chatClientFactory.Create(
            provider, artifacts.ModelId, artifacts.Credentials, artifacts.ProviderOptions);
        nuCodeServices.AddSingleton(chatClient);

        var nuCodeProvider = nuCodeServices.BuildServiceProvider();

        // Register Weave orchestration agents (Loom, Tapestry, Pattern, etc.)
        var agentRegistry = nuCodeProvider.GetRequiredService<IAgentProfileRegistry>();
        WeaveAgents.Register(agentRegistry);

        // Discover available models from the provider API (best-effort, non-blocking)
        var discoveredModels = await _modelDiscovery.DiscoverModelsAsync(
            provider, artifacts.Credentials, artifacts.ProviderOptions, ct).ConfigureAwait(false);

        var instanceId = $"nucode-{Guid.NewGuid():N}";

        var session = new NuCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: options.SessionId,
            workingDirectory: options.WorkingDirectory,
            provider: artifacts.ProviderId,
            modelId: artifacts.ModelId,
            discoveredModels: discoveredModels,
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
            _ = session.SendPromptAsync(options.InitialPrompt, null, ct)
                .ContinueWith(
                    t => LogInitialPromptFailed(t.Exception!),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        return session;
    }

    /// <inheritdoc />
    public Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        // TODO: Keep NuCode sessions in manual runtime mode until NuCode session state is
        // backed by a durable store and ResumeToken can be used to rehydrate the prior
        // NuCodeSession. Spawning a fresh in-process runtime is safe for the manual Resume
        // action, but it must not be treated as automatic lazy activation.
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

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct)
        => Task.FromResult(false);

    [LoggerMessage(Level = LogLevel.Error, Message = "Initial prompt processing failed")]
    private partial void LogInitialPromptFailed(Exception exception);
}
