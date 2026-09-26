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
using WeaveFleet.Application.Terminals;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// <see cref="IHarnessRuntime"/> for OpenCode 2. Fleet runs one private <c>opencode2 serve</c> per owner, with its
/// own password, for every directory; each Fleet session is a V2 session on it, and its resume token is the V2
/// session id. The server stays up while Fleet runs, and a stopped one is replaced on the next request.
/// Sessions on a profile run on a server of their own per profile version, which stops when it's idle
/// (<see cref="OpenCode2Servers"/>); every server shares the one database, so a session can move between them.
/// Fleet's plugin (canvas, app and browser tools) and skills load into it through <c>OPENCODE_CONFIG_CONTENT</c>, and a
/// profile through <c>OPENCODE_CONFIG</c> (<see cref="OpenCode2Profiles"/>).
/// The install it runs, and in separate mode its own config folder and database, come from <see cref="OpenCode2Install"/>.
/// </summary>
public sealed partial class OpenCode2HarnessRuntime : IHarnessRuntime, IAsyncDisposable, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> for V2 servers.</summary>
    public const string HttpClientName = "OpenCode2";

    /// <summary>
    /// The named <see cref="HttpClient"/> that passes a browser sign-in's callback on to V2's listener on this machine.
    /// Its address carries the provider's one-time code, so the client logs nothing and never uses a proxy.
    /// </summary>
    public const string SignInCallbackHttpClientName = "OpenCode2SignInCallback";

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
    private readonly OpenCode2Servers _servers;
    private readonly OpenCode2SignIn _signIn;

    // Profile version (content hash) → the Fleet profiles with that content, as sessions and composers asked for them,
    // so a change on a profile's server names the profiles the browser knows.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _profileIds = new(StringComparer.Ordinal);
    private Timer? _idleTimer;
    private string? _pluginFolder;
    private string? _skillsFolder;
    private readonly Lock _builtInSkillsSync = new();
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
        _servers = new OpenCode2Servers(
            (key, setup, ct) => StartServerAsync(key.OwnerUserId, setup, logLine: null, OnCatalogChanged, ct),
            TimeSpan.FromSeconds(Math.Max(1, options.Harness.OpenCode2ProfileServerIdleSeconds)),
            logger);
        // Sign-ins are in the database every server shares, so they go through the owner's server without a profile.
        _signIn = new OpenCode2SignIn(
            (owner, ct) => GetServerAsync(owner, profile: null, ct),
            () => Directory.CreateDirectory(Path.Combine(FleetDataDirectory(), "opencode2", "sign-in")).FullName,
            () => _install.Locate()?.Mode ?? _install.RememberedMode() ?? OpenCode2InstallMode.Default,
            () => httpClientFactory.CreateClient(SignInCallbackHttpClientName),
            TimeProvider.System);
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
    /// <remarks>
    /// V2's installer again, in the install's mode and with its HOME; V2 installed another way isn't Fleet's to update.
    /// Nothing when the program there isn't V2 any more: a default install OpenCode 1's installer replaced would have
    /// OpenCode 1 replaced in turn.
    /// </remarks>
    public HarnessCommand? GetUpdateCommand(HarnessAvailability availability, string? version)
        => availability is { ExecutablePath: { } path, Version: { } installed } && OpenCode2Executable.IsOpenCode2(installed)
            ? _install.UpdateCommand(path, version)
            : null;

    /// <inheritdoc />
    /// <remarks>
    /// Idle servers stop now and the next request starts one on the new binary; a busy one is replaced once it's idle.
    /// </remarks>
    public Task AfterUpdateAsync(CancellationToken ct) => _servers.AfterUpdateAsync(ct);

    /// <inheritdoc />
    /// <remarks>Writes the session's profile, if any, where its server will read it; that picks the server too.</remarks>
    public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
    {
        if (context.Profile is not { } profile)
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new OpenCode2LaunchArtifacts()));

        try
        {
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(
                new OpenCode2LaunchArtifacts { Profile = WriteProfile(profile) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.NotReady(
            [
                new RuntimePreparationError(
                    Code: "ProfileNotWritten",
                    Message: $"Fleet couldn't write the {profile.Name} profile for OpenCode 2: {ex.Message}",
                    Guidance: "Check that Fleet can write to its data folder."),
            ]));
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        var server = await GetServerAsync(options.OwnerUserId, ProfileOf(options.LaunchArtifacts), ct).ConfigureAwait(false);
        // On a server with the step tool, every session that isn't a workflow step has it denied.
        var created = await server.Client.CreateSessionAsync(
            options.WorkingDirectory, ct, hideStepTool: server.Setup.Workflows && !options.WorkflowStep).ConfigureAwait(false);

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

        LogSpawned(_logger, session.InstanceId, created.Id!, server.ProcessId ?? 0);
        return session;
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.WorkingDirectory);

        // A delegated child's V2 session lives on its parent's server, which is the only one that sends its events. The
        // child is prepared with the parent's profile as it is now, which is another server when the profile was edited
        // since the parent's server started.
        var server = (options.ParentSessionId is { } parentSessionId ? _servers.FindServing(options.OwnerUserId, parentSessionId) : null)
            ?? await GetServerAsync(options.OwnerUserId, ProfileOf(options.LaunchArtifacts), ct).ConfigureAwait(false);
        var info = await server.Client.GetSessionAsync(options.ResumeToken, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"OpenCode 2 has no session {options.ResumeToken}.");

        // A session made before workflows were on reaches a server with the step tool: deny it here too. Only Fleet's
        // own sessions, which it made allowing everything; a subagent's child keeps the rules its agent gave it.
        if (server.Setup.Workflows
            && !options.WorkflowStep
            && !options.DelegatedChild
            && options.ParentSessionId is null
            && string.IsNullOrEmpty(info.ParentID)
            && !(info.Permissions ?? []).Contains(OpenCode2HttpClient.DenyStepTool))
        {
            try
            {
                await server.Client.SetPermissionsAsync(
                    options.ResumeToken, [.. info.Permissions ?? OpenCode2HttpClient.AllowAll, OpenCode2HttpClient.DenyStepTool], ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // The session still works; it can see a tool Fleet refuses to take from it.
                LogStepToolNotHidden(_logger, options.ResumeToken, ex);
            }
        }

        // A subagent's child session attaches here too, and gets the events held for it since it started.
        var session = NewSession(
            info with { Id = options.ResumeToken },
            new OpenCode2SessionContext(options.SessionId, options.OwnerUserId, options.WorkingDirectory, options.ProjectId, options.ProjectName),
            server);
        LogResumed(_logger, session.InstanceId, options.ResumeToken, server.ProcessId ?? 0);
        return session;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read from the server a new session there on <paramref name="profile"/> would use, which serves every folder: a
    /// profile's providers, models and agents are its server's.
    /// </remarks>
    public async Task<HarnessCatalog?> GetCatalogAsync(string ownerUserId, string directory, HarnessProfile? profile, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(directory);
        var server = await GetServerAsync(ownerUserId, profile is null ? null : WriteProfile(profile), ct).ConfigureAwait(false);
        return await OpenCode2Catalog.ReadAsync(server, directory, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A throwaway V2 session in the folder, on the chosen model (else the folder's default), asked with <c>generate</c>
    /// as the recap is, so nothing is ever added to it; it's deleted when the conversation is disposed. V2's stateless
    /// <c>experimental/generate</c> can't be used: it reads only the server's base config, which doesn't have the
    /// providers Fleet's config gives sessions ("Model unavailable").
    /// </remarks>
    public async Task<IOffTheRecordConversation?> StartOffTheRecordAsync(OffTheRecordOptions options, CancellationToken ct)
    {
        HarnessHelpers.ValidateWorkingDirectory(options.Directory);
        var server = await GetServerAsync(options.OwnerUserId, options.Profile is null ? null : WriteProfile(options.Profile), ct).ConfigureAwait(false);
        var session = await server.Client.CreateSessionAsync(options.Directory, ct).ConfigureAwait(false);
        try
        {
            if (options is { ProviderId: { Length: > 0 } providerId, ModelId: { Length: > 0 } modelId })
            {
                await server.Client.SwitchModelAsync(
                    session.Id!, new OpenCode2ModelRef { Id = modelId, ProviderId = providerId, Variant = options.Variant }, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await DeleteThrowawayAsync(server, session.Id!).ConfigureAwait(false);
            throw;
        }

        return new OpenCode2OffTheRecordConversation(
            (prompt, token) => server.Client.GenerateAsync(session.Id!, prompt, token),
            ConversationQuestionTimeout,
            () => DeleteThrowawayAsync(server, session.Id!));
    }

    private async Task DeleteThrowawayAsync(OpenCode2Server server, string sessionId)
    {
        // Not the caller's token: the session must go even when the caller gave up.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await server.Client.DeleteSessionAsync(sessionId, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            LogThrowawayLeft(_logger, sessionId, ex);
        }
    }

    /// <summary>How long each question off the record may take: a workflow draft is a whole file.</summary>
    internal static readonly TimeSpan ConversationQuestionTimeout = TimeSpan.FromSeconds(120);

    /// <inheritdoc />
    /// <remarks>
    /// V2 never refuses a config: a broken profile starts, answers, and loads folders without the parts it couldn't
    /// use, and says so only in its log when a folder loads (<see cref="OpenCode2Profiles"/>). So the check starts a
    /// server of its own on the profile, the way a session's would start, with its log on; loads an empty folder; and
    /// reads the log, the profile as V2 stored it and the model a session would get. The server stops either way.
    /// </remarks>
    public async Task<HarnessProfileCheck> CheckProfileAsync(string ownerUserId, string content, CancellationToken ct)
    {
        if (OpenCode2Profiles.ReadSettings(content, out var settings) is { } unreadable)
            return unreadable;

        using (settings)
        {
            OpenCode2Profile profile;
            string folder;
            try
            {
                profile = WriteProfile(content);
                folder = Path.Combine(FleetDataDirectory(), "opencode2", "profile-check");
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new HarnessProfileCheck(false, $"Fleet couldn't write this profile for OpenCode 2: {ex.Message}");
            }

            var log = new ConcurrentQueue<OpenCode2Profiles.LogDiagnostic>();
            OpenCode2Server server;
            try
            {
                var setup = await GetSetupAsync(ownerUserId, profile).ConfigureAwait(false);
                server = await StartServerAsync(ownerUserId, setup, line =>
                {
                    if (OpenCode2Profiles.ParseLogLine(line) is { } diagnostic)
                        log.Enqueue(diagnostic);
                }, catalogChanged: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
            {
                return new HarnessProfileCheck(false, $"OpenCode 2 didn't start with this profile: {ex.Message}");
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.LoadLocationAsync(folder, ct).ConfigureAwait(false);
                    var config = await server.Client.GetConfigAsync(folder, ct).ConfigureAwait(false);
                    var model = await server.Client.GetDefaultModelAsync(folder, ct).ConfigureAwait(false);

                    // V2 logs while it loads the folder; its last lines can still be on their way.
                    await Task.Delay(ProfileCheckLogGrace, ct).ConfigureAwait(false);
                    return OpenCode2Profiles.Judge(profile.ConfigPath, settings!.RootElement, [.. config], model, [.. log]);
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    return new HarnessProfileCheck(false, $"OpenCode 2 started with this profile but didn't answer: {ex.Message}");
                }
            }
        }
    }

    /// <summary>How long a profile check waits after the folder loaded for the rest of V2's log.</summary>
    internal TimeSpan ProfileCheckLogGrace { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    /// <remarks>Over V2's integration API, on the owner's server without a profile (<see cref="OpenCode2SignIn"/>).</remarks>
    public IHarnessProviderSignIn? ProviderSignIn => _signIn;

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Whether <paramref name="bridgeToken"/> belongs to a server Fleet is running.</summary>
    internal bool IsBridgeToken(string bridgeToken) => FindServer(bridgeToken) is not null;

    /// <summary>The running server whose bridge token is <paramref name="bridgeToken"/>, if any.</summary>
    internal OpenCode2Server? FindServer(string bridgeToken)
    {
        var token = Encoding.UTF8.GetBytes(bridgeToken);
        return _servers.All.FirstOrDefault(server => server.IsRunning
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(server.BridgeToken), token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_idleTimer is not null)
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
        await _servers.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>For containers disposed synchronously; stops the servers the same way.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private OpenCode2HarnessSession NewSession(OpenCode2SessionInfo info, OpenCode2SessionContext context, OpenCode2Server server)
        => new(
            $"opencode2-{Guid.NewGuid():N}",
            info,
            context,
            server,
            // Back to a server like the one it's on (the same profile version) when that one stops.
            ct => GetServerAsync(context.OwnerUserId, server.Profile, ct),
            _analytics,
            new OpenCode2Delegations(_scopeFactory, context.OwnerUserId, context.FleetSessionId, _loggerFactory.CreateLogger<OpenCode2Delegations>()),
            _loggerFactory.CreateLogger<OpenCode2HarnessSession>());

    /// <summary>
    /// The owner's running server for <paramref name="profile"/> (none: the owner's own), started when there's none or
    /// the last one stopped. A server started with other settings (messages between sessions turned on or off) is
    /// replaced once nothing runs on it (no turn, no background shell); until then its sessions keep using it.
    /// Built-in skills aren't a setting of the server: they're files in a folder it watches.
    /// </summary>
    private async Task<OpenCode2Server> GetServerAsync(string ownerUserId, OpenCode2Profile? profile, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var setup = await GetSetupAsync(ownerUserId, profile).ConfigureAwait(false);
        var server = await _servers.GetAsync(OpenCode2ServerKey.For(ownerUserId, profile), setup, ct).ConfigureAwait(false);
        if (profile is not null)
            EnsureIdleTimer();
        return server;
    }

    private static OpenCode2Profile? ProfileOf(RuntimeLaunchArtifacts? artifacts)
        => (artifacts as OpenCode2LaunchArtifacts)?.Profile;

    private OpenCode2Profile WriteProfile(string content) => OpenCode2Profiles.Write(FleetDataDirectory(), content);

    /// <summary>Writes a Fleet profile for its server, and remembers which profile that server's catalog belongs to.</summary>
    private OpenCode2Profile WriteProfile(HarnessProfile profile)
    {
        var written = WriteProfile(profile.Content);
        _profileIds.GetOrAdd(written.Hash, static _ => new(StringComparer.Ordinal)).TryAdd(profile.Id, 0);
        return written;
    }

    /// <summary>
    /// What a server offers in a folder changed: tells the owner's browsers, naming the profiles and the sessions that
    /// get that catalog. A server's profile is a version of a Fleet profile, so it names every profile with that content.
    /// </summary>
    private void OnCatalogChanged(OpenCode2Server server, string directory)
    {
        IReadOnlyList<string> profileIds = server.Profile is { } profile
            ? _profileIds.TryGetValue(profile.Hash, out var ids) ? ids.Keys.Order(StringComparer.Ordinal).ToList() : []
            : [HarnessProfileService.NoProfile];
        var change = new HarnessCatalogChange(HarnessType, server.OwnerUserId, directory, profileIds, server.SessionsIn(directory));
        _ = PublishAsync();

        async Task PublishAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<HarnessCatalogChanges>() is { } changes)
                await changes.PublishAsync(change, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string FleetDataDirectory()
        => Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath)) ?? Environment.CurrentDirectory;

    /// <summary>Looks for idle profile servers to stop, from the first time one starts.</summary>
    private void EnsureIdleTimer()
    {
        if (Volatile.Read(ref _idleTimer) is not null)
            return;

        var interval = TimeSpan.FromTicks(Math.Clamp(_servers.ProfileIdleTimeout.Ticks / 4, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(1).Ticks));
        var timer = new Timer(_ => _ = StopIdleServersAsync(DateTimeOffset.UtcNow), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (Interlocked.CompareExchange(ref _idleTimer, timer, null) is not null)
        {
            timer.Dispose();
            return;
        }
        timer.Change(interval, interval);
    }

    /// <summary>Stops the profile servers idle at <paramref name="now"/>, and returns how many stopped.</summary>
    internal async Task<int> StopIdleServersAsync(DateTimeOffset now)
    {
        try
        {
            return await _servers.StopIdleAsync(now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogIdleStopFailed(_logger, ex);
            return 0;
        }
    }

    /// <summary>
    /// What the owner's server should start with. The tools and the Fleet API skill call back into Fleet, so they load
    /// only when the server can be told where Fleet is. The owner's built-in skills folder loads either way. The
    /// install is looked up on every request (files only), so a V2 installed while Fleet runs is found.
    /// </summary>
    private async Task<OpenCode2ServerSetup> GetSetupAsync(string ownerUserId, OpenCode2Profile? profile)
    {
        var fleetUrl = ResolveLocalFleetUrl();
        var (builtInSkills, sessionMessages, workflows) = await ReadOwnerSettingsAsync(ownerUserId).ConfigureAwait(false);

        string? plugin = null;
        List<string> skills = [];
        if (fleetUrl is not null)
        {
            plugin = InstallOnce(ref _pluginFolder, OpenCode2FleetFiles.InstallPlugin);

            // With auth on, Fleet doesn't trust requests from this machine, so the Fleet API skill couldn't reach it.
            if (!_options.Auth.Enabled && InstallOnce(ref _skillsFolder, OpenCode2FleetFiles.InstallSkills) is { } fleetSkills)
                skills.Add(fleetSkills);
        }

        // Named whatever is in it, so switching a built-in skill changes a folder V2 watches, not the server's setup.
        if (SyncBuiltInSkills(ownerUserId, builtInSkills) is { } builtIn)
            skills.Add(builtIn);

        var install = _install.Locate();
        return new OpenCode2ServerSetup(
            fleetUrl,
            OpenCode2FleetFiles.BuildConfigContent(plugin, skills),
            SessionMessages: sessionMessages && plugin is not null,
            install?.ExecutablePath,
            install?.Mode ?? OpenCode2InstallMode.Default,
            profile,
            Workflows: workflows && plugin is not null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes the owner's built-in skills folder again. Their servers watch it, so the sessions started afterwards get
    /// the change without a new server; sessions already running keep the skills they started with (V2 fixes a
    /// session's skill list when it's created).
    /// </remarks>
    public async Task BuiltInSkillsChangedAsync(string ownerUserId, CancellationToken ct)
    {
        var (builtInSkills, _, _) = await ReadOwnerSettingsAsync(ownerUserId).ConfigureAwait(false);
        SyncBuiltInSkills(ownerUserId, builtInSkills);
    }

    /// <summary>
    /// Makes the owner's built-in skills folder hold what they turned on and returns it, or <see langword="null"/> when
    /// it couldn't be written; servers then start without it, and the next request tries again.
    /// </summary>
    private string? SyncBuiltInSkills(string ownerUserId, IReadOnlyList<string> builtInSkills)
    {
        try
        {
            lock (_builtInSkillsSync)
                return OpenCode2FleetFiles.SyncBuiltInSkills(FleetDataDirectory(), ownerUserId, builtInSkills);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFleetFilesInstallFailed(_logger, ex);
            return null;
        }
    }

    /// <summary>
    /// The built-in skills the owner turned on that this Fleet ships, in name order, and whether messages between
    /// sessions and workflows are on.
    /// </summary>
    private async Task<(IReadOnlyList<string> BuiltInSkills, bool SessionMessages, bool Workflows)> ReadOwnerSettingsAsync(string ownerUserId)
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
        var workflows = scope.ServiceProvider.GetService<WorkflowsFeature>() is { } workflowsFeature
            && await workflowsFeature.IsEnabledAsync().ConfigureAwait(false);
        return (builtInSkills, sessionMessages, workflows);
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
            folder = install(FleetDataDirectory());
            Volatile.Write(ref installed, folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFleetFilesInstallFailed(_logger, ex);
            return null;
        }
    }

    /// <param name="logLine">Each line of V2's log, for a profile check; <see langword="null"/> leaves the log in V2's own file.</param>
    /// <param name="catalogChanged">Told when what the server offers in a folder Fleet uses changes; none for a profile check's.</param>
    private async Task<OpenCode2Server> StartServerAsync(
        string ownerUserId,
        OpenCode2ServerSetup setup,
        Action<string>? logLine,
        Action<OpenCode2Server, string>? catalogChanged,
        CancellationToken ct)
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

        // Under the folder's own config and Fleet's, over the user's (see OpenCode2Profiles).
        if (setup.Profile is { } profile)
            environment[OpenCode2Profiles.EnvironmentVariable] = profile.ConfigPath;
        if (setup.SessionMessages)
            environment[SessionMessages.EnvironmentVariable] = "1";
        if (setup.Workflows)
            environment[FleetWorkflows.EnvironmentVariable] = "1";

        // The agent's shell commands inherit the server's environment. Fleet's plugin takes out what's for the server
        // alone, in every shell V2 starts: an `opencode` the agent runs must not open this server's config and database,
        // nor drive its API with its password.
        environment[OpenCode2FleetFiles.ShellEnvironmentVariable] = OpenCode2FleetFiles.BuildShellEnvironment(
            TerminalEnvironment.AgentShellChanges(
                Environment.GetEnvironmentVariables(),
                [.. environment.Keys, "OPENCODE_SERVER_PASSWORD", OpenCode2FleetFiles.ShellEnvironmentVariable]));

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
                LogLine = logLine,
            }, ct).ConfigureAwait(false);

            client = new OpenCode2HttpClient(
                CreateHttpClient(baseUrl, password, RequestTimeout),
                CreateHttpClient(baseUrl, password, Timeout.InfiniteTimeSpan),
                _loggerFactory.CreateLogger<OpenCode2HttpClient>());

            // Listening isn't the same as answering: check the password works before sessions use it.
            var info = await client.GetInfoAsync(ct).ConfigureAwait(false);
            LogServerStarted(_logger, process.ProcessId ?? 0, info?.Version ?? version, baseUrl, setup.Profile?.Hash ?? "none");
            return new OpenCode2Server(ownerUserId, client, bridgeToken, process, _loggerFactory.CreateLogger<OpenCode2Server>(), setup)
            {
                CatalogChanged = catalogChanged,
            };
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

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId} ({Version}) listening on {BaseUrl}, profile {Profile}")]
    private static partial void LogServerStarted(ILogger logger, int processId, string version, Uri baseUrl, string profile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete OpenCode 2 session {SessionId}, made for a question off the record")]
    private static partial void LogThrowawayLeft(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't stop idle OpenCode 2 profile servers")]
    private static partial void LogIdleStopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't write Fleet's plugin or skills for OpenCode 2; its servers start without them")]
    private static partial void LogFleetFilesInstallFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't copy the skills to OpenCode 2's own config folder; its servers start without them")]
    private static partial void LogSkillSyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} created as {HarnessSessionId} on server {ProcessId}")]
    private static partial void LogSpawned(ILogger logger, string instanceId, string harnessSessionId, int processId);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} resumed {HarnessSessionId} on server {ProcessId}")]
    private static partial void LogResumed(ILogger logger, string instanceId, string harnessSessionId, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not hide the workflow step tool from OpenCode 2 session {HarnessSessionId}")]
    private static partial void LogStepToolNotHidden(ILogger logger, string harnessSessionId, Exception exception);
}
