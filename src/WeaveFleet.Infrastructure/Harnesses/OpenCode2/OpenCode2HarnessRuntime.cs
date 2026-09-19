using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// <see cref="IHarnessRuntime"/> for OpenCode 2. Fleet runs one private <c>opencode2 serve</c> per owner, with its
/// own password, for every directory; each Fleet session is a V2 session on it, and its resume token is the V2
/// session id. The server stays up while Fleet runs, and a stopped one is replaced on the next request.
/// Fleet's plugin (canvas, app and browser tools) and skills load into it through <c>OPENCODE_CONFIG_CONTENT</c>.
/// The install it runs, and in separate mode its own config folder and database, come from <see cref="OpenCode2Install"/>.
/// </summary>
public sealed partial class OpenCode2HarnessRuntime : IHarnessRuntime, IAsyncDisposable, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> for V2 servers.</summary>
    public const string HttpClientName = "OpenCode2";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Skills are local mode only, and belong to its one user.
    private const string LocalOwnerUserId = "local-user";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FleetOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenCode2HarnessRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analytics;
    private readonly OpenCode2Install _install;
    private readonly ConcurrentDictionary<string, OpenCode2Server> _servers = new(StringComparer.Ordinal);

    // Held while a server starts, so an owner never gets two; reads (bridge tokens) don't take it.
    private readonly SemaphoreSlim _serversLock = new(1, 1);
    private string? _pluginFolder;
    private string? _skillsFolder;
    private string? _builtInSkillsFolder;
    private OpenCode2InstallCheck? _lastCheck;
    private int _separateSkillsSynced;
    private bool _disposed;

    public OpenCode2HarnessRuntime(
        IHttpClientFactory httpClientFactory,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCode2HarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analytics = null)
        : this(httpClientFactory, options, scopeFactory, logger, loggerFactory, analytics, install: null)
    {
    }

    /// <summary>Test seam: <paramref name="install"/> in place of the one in the user's home folder.</summary>
    internal OpenCode2HarnessRuntime(
        IHttpClientFactory httpClientFactory,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCode2HarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analytics,
        OpenCode2Install? install)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _analytics = analytics;
        _install = install ?? new OpenCode2Install(
            ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable,
            ExecutableResolver.UserBinDirectories(),
            (path, ct) => HarnessProbe.CheckInstalledAsync("OpenCode 2", OpenCode2Executable.Command, path, logger, ct),
            OperatingSystem.IsWindows());
    }

    /// <inheritdoc />
    public string HarnessType => OpenCode2HarnessSession.Type;

    /// <summary>
    /// Variables every server starts with, over the install's (config folder, database) and under Fleet's own. Empty in
    /// Fleet; the live tests give V2 a scratch HOME, config and database this way, so it never touches the user's data.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ServerEnvironment { get; set; } = new Dictionary<string, string>();

    /// <inheritdoc />
    /// <remarks>Also decides the install mode the first time a working V2 turns up, and remembers it.</remarks>
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
    {
        var check = await _install.CheckAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _lastCheck, check);
        return check.Availability;
    }

    /// <inheritdoc />
    /// <remarks>
    /// V2's own installer in the install's mode (separate next to OpenCode 1), its sign-in, and the folders it uses.
    /// On Windows, where the installer doesn't run, the manual download.
    /// </remarks>
    public HarnessSetup GetSetup(HarnessAvailability availability)
    {
        var check = Volatile.Read(ref _lastCheck)
            ?? new OpenCode2InstallCheck(_install.Locate()?.Mode ?? OpenCode2InstallMode.Default, Remembered: false, availability);
        return _install.Setup(check with { Availability = availability });
    }

    /// <inheritdoc />
    public string LatestVersionPackage => "@opencode/cli";

    /// <inheritdoc />
    /// <remarks>
    /// The oldest version that passes a check of every endpoint and event the adapter uses (Stage 5 in
    /// <c>.weave/plans/opencode2-harness.md</c>): 2.0.0–2.0.5 have no <c>/api/info</c>, which a server start calls.
    /// </remarks>
    public string MinimumVersion => MinimumOpenCode2Version;

    internal const string MinimumOpenCode2Version = "2.0.6";

    /// <inheritdoc />
    /// <remarks>V2's installer again, in the install's mode and with its HOME; V2 installed another way isn't Fleet's to update.</remarks>
    public HarnessCommand? GetUpdateCommand(HarnessAvailability availability, string? version)
        => availability.ExecutablePath is { } path ? _install.UpdateCommand(path, version) : null;

    /// <inheritdoc />
    /// <remarks>
    /// Idle servers stop now and the next request starts one on the new binary; a busy one is replaced once it's idle.
    /// </remarks>
    public async Task AfterUpdateAsync(CancellationToken ct)
    {
        await _serversLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (owner, server) in _servers)
            {
                if (server.IsRunning && !await server.IsIdleAsync(ct).ConfigureAwait(false))
                {
                    server.MarkOutdated();
                    continue;
                }
                _servers.TryRemove(owner, out _);
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _serversLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
        => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new OpenCode2LaunchArtifacts()));

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var server = await GetServerAsync(options.OwnerUserId, ct).ConfigureAwait(false);
        var created = await server.Client.CreateSessionAsync(options.WorkingDirectory, ct).ConfigureAwait(false);

        var session = NewSession(
            created,
            new OpenCode2SessionContext(options.SessionId, options.OwnerUserId, options.WorkingDirectory, options.ProjectId, options.ProjectName),
            server);
        try
        {
            if (options.InitialPrompt is not null)
                await session.SendPromptAsync(options.InitialPrompt, null, ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        LogSpawned(_logger, session.InstanceId, created.Id!);
        return session;
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var server = await GetServerAsync(options.OwnerUserId, ct).ConfigureAwait(false);
        var info = await server.Client.GetSessionAsync(options.ResumeToken, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"OpenCode 2 has no session {options.ResumeToken}.");

        // A subagent's child session attaches here too, and gets the events held for it since it started.
        var session = NewSession(
            info with { Id = options.ResumeToken },
            new OpenCode2SessionContext(options.SessionId, options.OwnerUserId, options.WorkingDirectory, options.ProjectId, options.ProjectName),
            server);
        LogResumed(_logger, session.InstanceId, options.ResumeToken);
        return session;
    }

    /// <inheritdoc />
    /// <remarks>Read from the owner's server, which serves every folder: the one a new session there would use.</remarks>
    public async Task<HarnessCatalog?> GetCatalogAsync(string ownerUserId, string directory, HarnessProfile? profile, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(directory);
        var server = await GetServerAsync(ownerUserId, ct).ConfigureAwait(false);
        return await OpenCode2Catalog.ReadAsync(server, directory, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Whether <paramref name="bridgeToken"/> belongs to a server Fleet is running.</summary>
    internal bool IsBridgeToken(string bridgeToken) => FindServer(bridgeToken) is not null;

    /// <summary>The running server whose bridge token is <paramref name="bridgeToken"/>, if any.</summary>
    internal OpenCode2Server? FindServer(string bridgeToken)
    {
        var token = Encoding.UTF8.GetBytes(bridgeToken);
        return _servers.Values.FirstOrDefault(server => server.IsRunning
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(server.BridgeToken), token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _serversLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var server in _servers.Values)
                await server.DisposeAsync().ConfigureAwait(false);
            _servers.Clear();
        }
        finally
        {
            _serversLock.Release();
        }
    }

    /// <summary>For containers disposed synchronously; stops the servers the same way.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private OpenCode2HarnessSession NewSession(OpenCode2SessionInfo info, OpenCode2SessionContext context, OpenCode2Server server)
        => new(
            $"opencode2-{Guid.NewGuid():N}",
            info,
            context,
            server,
            ct => GetServerAsync(context.OwnerUserId, ct),
            _analytics,
            new OpenCode2Delegations(_scopeFactory, context.OwnerUserId, context.FleetSessionId, _loggerFactory.CreateLogger<OpenCode2Delegations>()),
            _loggerFactory.CreateLogger<OpenCode2HarnessSession>());

    /// <summary>
    /// The owner's running server, started when there's none or the last one stopped. A server started with other
    /// settings (a built-in skill switched, messages between sessions turned on or off) is replaced when none of its
    /// sessions is running a turn; until then the owner keeps using it.
    /// </summary>
    private async Task<OpenCode2Server> GetServerAsync(string ownerUserId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var setup = await GetSetupAsync(ownerUserId).ConfigureAwait(false);

        await _serversLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_servers.TryGetValue(ownerUserId, out var existing))
            {
                if (existing.IsRunning
                    && ((existing.Setup == setup && !existing.IsOutdated) || !await existing.IsIdleAsync(ct).ConfigureAwait(false)))
                    return existing;

                if (existing.IsRunning)
                    LogReplacingServer(_logger, existing.ProcessId ?? 0);
                _servers.TryRemove(ownerUserId, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var server = await StartServerAsync(ownerUserId, setup, ct).ConfigureAwait(false);
            _servers[ownerUserId] = server;
            return server;
        }
        finally
        {
            _serversLock.Release();
        }
    }

    /// <summary>
    /// What the owner's server should start with. The tools and the Fleet API skill call back into Fleet, so they load
    /// only when the server can be told where Fleet is. The built-in skills the owner turned on load either way. The
    /// install is looked up on every request (files only), so a V2 installed while Fleet runs is found.
    /// </summary>
    private async Task<OpenCode2ServerSetup> GetSetupAsync(string ownerUserId)
    {
        var fleetUrl = ResolveLocalFleetUrl();
        var (builtInSkills, sessionMessages) = await ReadOwnerSettingsAsync(ownerUserId).ConfigureAwait(false);

        string? plugin = null;
        List<string> skills = [];
        if (fleetUrl is not null)
        {
            plugin = InstallOnce(ref _pluginFolder, OpenCode2FleetFiles.InstallPlugin);

            // With auth on, Fleet doesn't trust requests from this machine, so the Fleet API skill couldn't reach it.
            if (!_options.Auth.Enabled && InstallOnce(ref _skillsFolder, OpenCode2FleetFiles.InstallSkills) is { } fleetSkills)
                skills.Add(fleetSkills);
        }

        if (builtInSkills.Count > 0 && InstallOnce(ref _builtInSkillsFolder, OpenCode2FleetFiles.InstallBuiltInSkills) is { } builtIn)
            skills.AddRange(builtInSkills.Select(name => Path.Combine(builtIn, name)));

        var install = _install.Locate();
        return new OpenCode2ServerSetup(
            fleetUrl,
            OpenCode2FleetFiles.BuildConfigContent(plugin, skills),
            SessionMessages: sessionMessages && plugin is not null,
            install?.ExecutablePath,
            install?.Mode ?? OpenCode2InstallMode.Default);
    }

    /// <summary>The built-in skills the owner turned on that this Fleet ships, in name order, and whether messages between sessions are on.</summary>
    private async Task<(IReadOnlyList<string> BuiltInSkills, bool SessionMessages)> ReadOwnerSettingsAsync(string ownerUserId)
    {
        using var userScope = BackgroundUserContext.BeginScope(ownerUserId);
        using var scope = _scopeFactory.CreateScope();

        IReadOnlyList<string> builtInSkills = [];
        if (scope.ServiceProvider.GetService<IUserPreferenceRepository>() is { } preferences)
        {
            var enabled = await BuiltInSkillService.GetEnabledAsync(preferences).ConfigureAwait(false);
            builtInSkills = enabled.Where(OpenCode2FleetFiles.BuiltInSkillNames.Contains).Order(StringComparer.Ordinal).ToList();
        }

        var sessionMessages = scope.ServiceProvider.GetService<SessionMessagesFeature>() is { } feature
            && await feature.IsEnabledAsync().ConfigureAwait(false);
        return (builtInSkills, sessionMessages);
    }

    /// <summary>
    /// Writes Fleet's files for V2 on first use and returns their folder, or <see langword="null"/> when they couldn't be
    /// written; servers then start without them, and the next start tries again.
    /// </summary>
    private string? InstallOnce(ref string? installed, Func<string, string> install)
    {
        if (Volatile.Read(ref installed) is { } folder)
            return folder;

        try
        {
            folder = install(Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath)) ?? Environment.CurrentDirectory);
            Volatile.Write(ref installed, folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFleetFilesInstallFailed(_logger, ex);
            return null;
        }
    }

    private async Task<OpenCode2Server> StartServerAsync(string ownerUserId, OpenCode2ServerSetup setup, CancellationToken ct)
    {
        var executable = setup.ExecutablePath
            ?? throw new InvalidOperationException("OpenCode 2 isn't installed. Set it up in Settings → Harnesses.");

        // The server would run on the user's own OpenCode data, so make sure it's the API this harness speaks.
        var probe = await HarnessProbe.RunAsync(executable, ["--version"], ct).ConfigureAwait(false);
        var version = HarnessProbe.ParseVersion(probe.StandardOutput);
        if (version is null || !OpenCode2Executable.IsOpenCode2(version))
            throw new InvalidOperationException($"{executable} is OpenCode {version ?? "(unknown version)"}, not OpenCode 2.");

        // Its sessions now live in this mode's database, so the mode stays even if an OpenCode 1 turns up later.
        if (_install.RememberedMode() is null)
            _install.Remember(setup.Mode);

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var bridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        // The agent reaches Fleet under a path that names this process, so Fleet can tell its calls from the user's.
        var environment = new Dictionary<string, string>(_install.PrepareEnvironment(setup.Mode), StringComparer.Ordinal)
        {
            ["FLEET_BRIDGE_TOKEN"] = bridgeToken,
        };
        foreach (var (name, value) in ServerEnvironment)
            environment[name] = value;
        if (setup.FleetUrl is { } fleetUrl)
            environment["FLEET_URL"] = $"{fleetUrl.TrimEnd('/')}{SessionMessages.AgentPathPrefix}/{bridgeToken}";
        if (setup.ConfigContent is { } configContent)
            environment["OPENCODE_CONFIG_CONTENT"] = configContent;
        if (setup.SessionMessages)
            environment[SessionMessages.EnvironmentVariable] = "1";

        // Once its config folder exists, the skills copy there (see HarnessInstallPaths.SkillTargets).
        if (setup.Mode == OpenCode2InstallMode.Separate)
            await SyncSkillsToSeparateInstallAsync(ct).ConfigureAwait(false);

        var process = new OpenCode2ProcessManager(_loggerFactory.CreateLogger<OpenCode2ProcessManager>());
        OpenCode2HttpClient? client = null;
        try
        {
            var baseUrl = await process.StartAsync(new OpenCode2ProcessOptions
            {
                ExecutablePath = executable,
                WorkingDirectory = ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory,
                Password = password,
                EnvironmentVariables = environment,
                StartupTimeout = TimeSpan.FromSeconds(_options.HarnessStartupTimeoutSeconds),
            }, ct).ConfigureAwait(false);

            client = new OpenCode2HttpClient(
                CreateHttpClient(baseUrl, password, RequestTimeout),
                CreateHttpClient(baseUrl, password, Timeout.InfiniteTimeSpan),
                _loggerFactory.CreateLogger<OpenCode2HttpClient>());

            // Listening isn't the same as answering: check the password works before sessions use it.
            var info = await client.GetInfoAsync(ct).ConfigureAwait(false);
            LogServerStarted(_logger, process.ProcessId ?? 0, info?.Version ?? version, baseUrl);
            return new OpenCode2Server(ownerUserId, client, bridgeToken, process, _loggerFactory.CreateLogger<OpenCode2Server>(), setup);
        }
        catch
        {
            client?.Dispose();
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A separate install reads its own config folder, not <c>~/.config/opencode</c>, so the first server Fleet starts
    /// copies the user's skills there: the install may be newer than the skills. Later installs copy as they happen.
    /// </summary>
    private async Task SyncSkillsToSeparateInstallAsync(CancellationToken ct)
    {
        if (_options.Auth.Enabled || Interlocked.Exchange(ref _separateSkillsSynced, 1) == 1)
            return;

        try
        {
            using var userScope = BackgroundUserContext.BeginScope(LocalOwnerUserId);
            using var scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<ISkillSyncEngine>() is { } skills)
                await skills.SyncHarnessAsync(HarnessInstallPaths.OpenCode2, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Volatile.Write(ref _separateSkillsSynced, 0);
            LogSkillSyncFailed(_logger, ex);
        }
    }

    private HttpClient CreateHttpClient(Uri baseUrl, string password, TimeSpan timeout)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = baseUrl;
        client.Timeout = timeout;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}")));
        return client;
    }

    private string? ResolveLocalFleetUrl()
    {
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<ILocalFleetUrl>()?.TryGet();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId} ({Version}) listening on {BaseUrl}")]
    private static partial void LogServerStarted(ILogger logger, int processId, string version, Uri baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId} started with other settings (built-in skills, messages between sessions) and is idle; replacing it")]
    private static partial void LogReplacingServer(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't write Fleet's plugin or skills for OpenCode 2; its servers start without them")]
    private static partial void LogFleetFilesInstallFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't copy the skills to OpenCode 2's own config folder; its servers start without them")]
    private static partial void LogSkillSyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} created as {HarnessSessionId}")]
    private static partial void LogSpawned(ILogger logger, string instanceId, string harnessSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} resumed {HarnessSessionId}")]
    private static partial void LogResumed(ILogger logger, string instanceId, string harnessSessionId);
}
