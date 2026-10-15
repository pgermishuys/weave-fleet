using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.Acp;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Harnesses.Copilot;

/// <summary>What preparing a GitHub Copilot session decided: the model it starts on, when the user picked one.</summary>
internal sealed record CopilotLaunchArtifacts(string? ModelId) : RuntimeLaunchArtifacts;

/// <summary><see cref="IHarnessRuntime"/> for GitHub Copilot: one weave-copilot process per session, spoken to over ACP.</summary>
public sealed class CopilotHarnessRuntime(IServiceScopeFactory scopes, ILoggerFactory loggers) : IHarnessRuntime
{
    /// <summary>The preference for a path to weave-copilot, for when it isn't on PATH.</summary>
    public const string PathPreferenceKey = "copilot.path";

    internal const string FeatureOffMessage = "GitHub Copilot is experimental: turn it on in Settings → Features.";

    private const string Executable = "weave-copilot";
    private const string LocalUser = "local-user";
    private static readonly TimeSpan CatalogLife = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger = loggers.CreateLogger<CopilotHarnessRuntime>();
    private readonly ConcurrentDictionary<bool, (DateTimeOffset At, HarnessCatalog Catalog)> _catalogs = new();

    /// <inheritdoc />
    public string HarnessType => CopilotHarness.TypeName;

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        if (!await IsOnAsync(LocalUser, CopilotHarness.FeaturePreferenceKey).ConfigureAwait(false))
            return HarnessAvailability.NotWorking(FeatureOffMessage);
        return await HarnessProbe.CheckInstalledAsync("weave-copilot", await ExecutableAsync(LocalUser).ConfigureAwait(false), [], _logger, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
        => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new CopilotLaunchArtifacts(context.ModelId)));

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        await RequireFeatureAsync(options.OwnerUserId).ConfigureAwait(false);
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
        var agent = await StartAsync(options.OwnerUserId, options.WorkingDirectory, ct).ConfigureAwait(false);
        try
        {
            var created = await NewSessionAsync(agent, options.WorkingDirectory, ct).ConfigureAwait(false);
            var session = new AcpHarnessSession(HarnessType, $"copilot-{Guid.NewGuid():N}", options.SessionId, AcpMapper.Str(created, "sessionId")!,
                options.WorkingDirectory, agent, _logger);
            session.ReadModels(created);
            if (options.InitialPrompt is not null)
            {
                var model = (options.LaunchArtifacts as CopilotLaunchArtifacts)?.ModelId;
                await session.SendPromptAsync(options.InitialPrompt, model is null ? null : new PromptOptions { ModelId = model }, ct).ConfigureAwait(false);
            }

            return session;
        }
        catch
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        await RequireFeatureAsync(options.OwnerUserId).ConfigureAwait(false);
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
        var agent = await StartAsync(options.OwnerUserId, options.WorkingDirectory, ct).ConfigureAwait(false);
        try
        {
            var session = new AcpHarnessSession(HarnessType, $"copilot-{Guid.NewGuid():N}", options.SessionId, options.ResumeToken, options.WorkingDirectory, agent, _logger);
            await session.LoadAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct) => Task.FromResult(false);

    /// <inheritdoc />
    public async Task<HarnessCatalog?> GetCatalogAsync(string ownerUserId, string directory, HarnessProfile? profile, CancellationToken ct)
    {
        if (!await IsOnAsync(ownerUserId, CopilotHarness.FeaturePreferenceKey).ConfigureAwait(false))
            return null;
        var hydra = await IsOnAsync(ownerUserId, CopilotHarness.HydraFusionPreferenceKey).ConfigureAwait(false);
        if (_catalogs.TryGetValue(hydra, out var cached) && DateTimeOffset.UtcNow - cached.At < CatalogLife)
            return cached.Catalog;

        await using var agent = await StartAsync(ownerUserId, Directory.Exists(directory) ? directory : Environment.CurrentDirectory, ct).ConfigureAwait(false);
        // The model list without a session: a session/new would leave one in the user's own Copilot history each time.
        var listed = await agent.Client.RequestAsync("_fleet/models", new JsonObject(), ct).ConfigureAwait(false);
        var (models, current) = AcpHarnessSession.ParseModelList(listed);
        var catalog = new HarnessCatalog
        {
            Agents = [],
            Providers = [new ProviderInfo { Id = "copilot", Name = "GitHub Copilot", Models = models }],
            DefaultModelProviderId = "copilot",
            DefaultModelId = current,
        };
        _catalogs[hydra] = (DateTimeOffset.UtcNow, catalog);
        return catalog;
    }

    private async Task<AcpAgentProcess> StartAsync(string ownerUserId, string workingDirectory, CancellationToken ct)
    {
        List<string> arguments = [];
        if (await IsOnAsync(ownerUserId, CopilotHarness.HydraFusionPreferenceKey).ConfigureAwait(false))
            arguments.Add("--hydrafusion");

        var agent = AcpAgentProcess.Start(await ExecutableAsync(ownerUserId).ConfigureAwait(false), arguments, workingDirectory,
            new Dictionary<string, string>(), loggers.CreateLogger<AcpAgentProcess>());
        try
        {
            await agent.Client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                    ["terminal"] = false,
                },
                ["clientInfo"] = new JsonObject { ["name"] = "weave-fleet", ["version"] = "1" },
            }, ct).ConfigureAwait(false);
            return agent;
        }
        catch (AcpException ex)
        {
            var stderr = agent.Stderr;
            await agent.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(stderr.Length > 0 ? $"{ex.Message} {stderr}" : ex.Message, ex);
        }
    }

    private static Task<JsonElement> NewSessionAsync(AcpAgentProcess agent, string directory, CancellationToken ct)
        => agent.Client.RequestAsync("session/new", new JsonObject { ["cwd"] = directory, ["mcpServers"] = new JsonArray() }, ct);

    private async Task RequireFeatureAsync(string ownerUserId)
    {
        if (!await IsOnAsync(ownerUserId, CopilotHarness.FeaturePreferenceKey).ConfigureAwait(false))
            throw new InvalidOperationException(FeatureOffMessage);
    }

    private async Task<bool> IsOnAsync(string userId, string key)
        => string.Equals(await ReadPreferenceAsync(userId, key).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>weave-copilot's path: the user's setting, else the one named in <c>FLEET_WEAVE_COPILOT</c>, else PATH.</summary>
    private async Task<string> ExecutableAsync(string userId)
    {
        var saved = await ReadPreferenceAsync(userId, PathPreferenceKey).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(saved))
            return saved;
        return Environment.GetEnvironmentVariable("FLEET_WEAVE_COPILOT") is { Length: > 0 } fromEnv ? fromEnv : Executable;
    }

    private async Task<string?> ReadPreferenceAsync(string userId, string key)
    {
        using var user = BackgroundUserContext.BeginScope(userId);
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync(key).ConfigureAwait(false);
    }
}
