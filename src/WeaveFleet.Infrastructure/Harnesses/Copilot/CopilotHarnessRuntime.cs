using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
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
public sealed class CopilotHarnessRuntime(
    IServiceScopeFactory scopes,
    ILoggerFactory loggers,
    IAnalyticsCollector? analytics = null,
    IHttpClientFactory? httpClients = null,
    string? home = null) : IHarnessRuntime
{
    private static readonly TimeSpan AuthLife = TimeSpan.FromSeconds(30);

    /// <summary>The preference for a path to weave-copilot, for when it isn't on PATH.</summary>
    public const string PathPreferenceKey = "copilot.path";

    internal const string CliMissingMessage = "Install the Copilot CLI from Settings, or put copilot on PATH.";

    internal const string FeatureOffMessage = "GitHub Copilot is experimental: turn it on in Settings → Features.";

    private const string Executable = "weave-copilot";
    private const string LocalUser = "local-user";
    private static readonly TimeSpan CatalogLife = TimeSpan.FromMinutes(10);

    /// <summary>The tokens of the weave-copilot processes running a session, and the MCP server they use for Fleet's tools.</summary>
    internal CopilotFleetTools FleetTools { get; } = new();

    private readonly ILogger _logger = loggers.CreateLogger<CopilotHarnessRuntime>();
    private readonly ConcurrentDictionary<bool, (DateTimeOffset At, HarnessCatalog Catalog)> _catalogs = new();
    private (DateTimeOffset At, bool SignedIn)? _auth;

    /// <inheritdoc />
    /// <remarks>Over weave-copilot's <c>_fleet/auth/*</c>, which drives GitHub's own <c>copilot login</c> (<see cref="CopilotSignIn"/>).</remarks>
    public IHarnessProviderSignIn? ProviderSignIn => _signInInstance ??= new CopilotSignIn(
        StartPlainAsync, () => _auth = null, TimeProvider.System);
    private CopilotSignIn? _signInInstance;

    private CopilotInstaller? _installer;

    private CopilotInstaller InstallerInstance => _installer ??= new CopilotInstaller(
        httpClients ?? new DefaultHttpClientFactory(), home ?? ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory);

    /// <inheritdoc />
    /// <remarks>Fleet downloads weave-copilot and the Copilot CLI at the versions this release pins, rather than asking the user to.</remarks>
    public IHarnessInstaller Installer => InstallerInstance;

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <inheritdoc />
    public string HarnessType => CopilotHarness.TypeName;

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        if (!await IsOnAsync(LocalUser, CopilotHarness.FeaturePreferenceKey).ConfigureAwait(false))
            return HarnessAvailability.NotWorking(FeatureOffMessage);
        var installed = await HarnessProbe.CheckInstalledAsync("weave-copilot", await ExecutableAsync(LocalUser).ConfigureAwait(false), [], _logger, ct).ConfigureAwait(false);
        if (!installed.Available)
            return installed;
        if (!CliFound(InstallerInstance.ManagedCli(), await ReadPreferenceAsync(LocalUser, PathPreferenceKey).ConfigureAwait(false),
                Environment.GetEnvironmentVariable("WEAVE_COPILOT_CLI"), Environment.GetEnvironmentVariable("PATH")))
            return HarnessAvailability.NotInstalled(CliMissingMessage);
        if (await IsSignedInAsync(ct).ConfigureAwait(false))
            return installed;
        return HarnessAvailability.SignInRequired(SignInMessage, installed.Version, installed.ExecutablePath);
    }

    /// <summary>
    /// Whether weave-copilot will find the Copilot CLI: the one Fleet installed (handed over unless the user runs their own
    /// weave-copilot), the one <c>WEAVE_COPILOT_CLI</c> names, or <c>copilot</c> on PATH. The same order weave-copilot looks in.
    /// </summary>
    internal static bool CliFound(string? managedCli, string? customWeaveCopilotPath, string? envCli, string? pathEnv)
    {
        if (managedCli is not null && string.IsNullOrWhiteSpace(customWeaveCopilotPath))
            return true;
        if (!string.IsNullOrEmpty(envCli))
            return File.Exists(envCli);
        return ExecutableResolver.TryResolve("copilot", pathEnv, [], out _);
    }

    /// <summary>What weave-copilot said when it couldn't start, as Fleet says it: a missing CLI gets what to do about it.</summary>
    internal static HarnessStartException StartFailure(Exception ex)
        => new(ex.Message.Contains("Copilot CLI not found", StringComparison.Ordinal) ? CliMissingMessage : ex.Message, ex);

    internal const string SignInMessage = "Sign in to GitHub Copilot.";

    /// <summary>Whether Copilot is signed in, kept for a moment; when weave-copilot can't say, it isn't held against the harness.</summary>
    private async Task<bool> IsSignedInAsync(CancellationToken ct)
    {
        if (_auth is { } known && DateTimeOffset.UtcNow - known.At < AuthLife)
            return known.SignedIn;
        try
        {
            var status = await CopilotAuthStatus.AskAsync(StartPlainAsync, ct).ConfigureAwait(false);
            _auth = (DateTimeOffset.UtcNow, status.Authenticated);
            return status.Authenticated;
        }
        catch (Exception ex) when (ex is AcpException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>weave-copilot for the sign-in, without HydraFusion's flag: it only asks about and changes the sign-in.</summary>
    private async Task<IAcpAgent> StartPlainAsync(CancellationToken ct)
        => await StartAsync(LocalUser, Environment.CurrentDirectory, ct, withHydraFusion: false).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
        => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new CopilotLaunchArtifacts(context.ModelId)));

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        await RequireFeatureAsync(options.OwnerUserId).ConfigureAwait(false);
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
        var agent = await StartAsync(options.OwnerUserId, options.WorkingDirectory, ct).ConfigureAwait(false);
        var token = FleetTools.Issue(options.SessionId, options.OwnerUserId);
        try
        {
            var created = await NewSessionAsync(agent, options.WorkingDirectory, CopilotFleetTools.McpServers(LocalFleetUrl(), token), ct).ConfigureAwait(false);
            var session = new AcpHarnessSession(HarnessType, $"copilot-{Guid.NewGuid():N}", options.SessionId, AcpMapper.Str(created, "sessionId")!,
                options.WorkingDirectory, agent, _logger, analytics, options.ProjectId, options.ProjectName, options.OwnerUserId) { OnEnded = () => FleetTools.Revoke(token) };
            session.ReadModels(created);
            if (options.InitialPrompt is not null)
            {
                var model = (options.LaunchArtifacts as CopilotLaunchArtifacts)?.ModelId;
                await session.SendPromptAsync(options.InitialPrompt, model is null ? null : new PromptOptions { ModelId = model }, ct).ConfigureAwait(false);
            }

            return session;
        }
        catch (Exception ex)
        {
            FleetTools.Revoke(token);
            await agent.DisposeAsync().ConfigureAwait(false);
            if (ex is AcpException)
                throw StartFailure(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        await RequireFeatureAsync(options.OwnerUserId).ConfigureAwait(false);
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);
        var agent = await StartAsync(options.OwnerUserId, options.WorkingDirectory, ct).ConfigureAwait(false);
        var token = FleetTools.Issue(options.SessionId, options.OwnerUserId);
        try
        {
            var session = new AcpHarnessSession(HarnessType, $"copilot-{Guid.NewGuid():N}", options.SessionId, options.ResumeToken, options.WorkingDirectory, agent, _logger,
                analytics, options.ProjectId, options.ProjectName, options.OwnerUserId) { OnEnded = () => FleetTools.Revoke(token) };
            await session.LoadAsync(CopilotFleetTools.McpServers(LocalFleetUrl(), token), ct).ConfigureAwait(false);
            return session;
        }
        catch (Exception ex)
        {
            FleetTools.Revoke(token);
            await agent.DisposeAsync().ConfigureAwait(false);
            if (ex is AcpException)
                throw StartFailure(ex);
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

    private async Task<AcpAgentProcess> StartAsync(string ownerUserId, string workingDirectory, CancellationToken ct, bool withHydraFusion = true)
    {
        List<string> arguments = [];
        if (withHydraFusion && await IsOnAsync(ownerUserId, CopilotHarness.HydraFusionPreferenceKey).ConfigureAwait(false))
            arguments.Add("--hydrafusion");

        if (InstallerInstance.ManagedCli() is { } cli && string.IsNullOrWhiteSpace(await ReadPreferenceAsync(ownerUserId, PathPreferenceKey).ConfigureAwait(false)))
            arguments.AddRange(["--copilot-cli", cli]);

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
            throw StartFailure(stderr.Length > 0 ? new InvalidOperationException($"{ex.Message} {stderr}", ex) : ex);
        }
    }

    private static Task<JsonElement> NewSessionAsync(AcpAgentProcess agent, string directory, JsonArray mcpServers, CancellationToken ct)
        => agent.Client.RequestAsync("session/new", new JsonObject { ["cwd"] = directory, ["mcpServers"] = mcpServers }, ct);

    /// <summary>Where Fleet listens, for the MCP server; null until it knows.</summary>
    private string? LocalFleetUrl()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ILocalFleetUrl>()?.TryGet();
    }

    private async Task RequireFeatureAsync(string ownerUserId)
    {
        if (!await IsOnAsync(ownerUserId, CopilotHarness.FeaturePreferenceKey).ConfigureAwait(false))
            throw new InvalidOperationException(FeatureOffMessage);
    }

    private async Task<bool> IsOnAsync(string userId, string key)
        => string.Equals(await ReadPreferenceAsync(userId, key).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>weave-copilot's path: the user's setting, else the copy Fleet installed, else the one named in <c>FLEET_WEAVE_COPILOT</c>, else PATH.</summary>
    private async Task<string> ExecutableAsync(string userId)
    {
        var saved = await ReadPreferenceAsync(userId, PathPreferenceKey).ConfigureAwait(false);
        var named = !string.IsNullOrWhiteSpace(saved) ? saved
            : InstallerInstance.ManagedWeaveCopilot() is { } managed ? managed
            : Environment.GetEnvironmentVariable("FLEET_WEAVE_COPILOT") is { Length: > 0 } fromEnv ? fromEnv : Executable;
        // Resolved as the availability probe does, so a .cmd shim on Windows runs, not only a bare weave-copilot.exe.
        return ExecutableResolver.Resolve(named);
    }

    private async Task<string?> ReadPreferenceAsync(string userId, string key)
    {
        using var user = BackgroundUserContext.BeginScope(userId);
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync(key).ConfigureAwait(false);
    }
}
