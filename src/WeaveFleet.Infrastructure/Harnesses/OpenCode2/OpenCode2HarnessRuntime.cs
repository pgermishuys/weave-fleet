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
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// <see cref="IHarnessRuntime"/> for OpenCode 2. Fleet runs one private <c>opencode2 serve</c> per owner, with its
/// own password, for every directory; each Fleet session is a V2 session on it, and its resume token is the V2
/// session id. The server stays up while Fleet runs, and a stopped one is replaced on the next request.
/// </summary>
public sealed partial class OpenCode2HarnessRuntime : IHarnessRuntime, IAsyncDisposable, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> for V2 servers.</summary>
    public const string HttpClientName = "OpenCode2";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FleetOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenCode2HarnessRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analytics;
    private readonly ConcurrentDictionary<string, OpenCode2Server> _servers = new(StringComparer.Ordinal);

    // Held while a server starts, so an owner never gets two; reads (bridge tokens) don't take it.
    private readonly SemaphoreSlim _serversLock = new(1, 1);
    private bool _disposed;

    public OpenCode2HarnessRuntime(
        IHttpClientFactory httpClientFactory,
        FleetOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenCode2HarnessRuntime> logger,
        ILoggerFactory loggerFactory,
        IAnalyticsCollector? analytics = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _analytics = analytics;
    }

    /// <inheritdoc />
    public string HarnessType => OpenCode2HarnessSession.Type;

    /// <inheritdoc />
    public async Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
        => OpenCode2Executable.RequireOpenCode2(await HarnessProbe.CheckInstalledAsync(
            "OpenCode 2", OpenCode2Executable.Command, OpenCode2Executable.InstallDirectories(), _logger, ct).ConfigureAwait(false));

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
            created.Id!,
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
        if (await server.Client.GetSessionAsync(options.ResumeToken, ct).ConfigureAwait(false) is null)
            throw new InvalidOperationException($"OpenCode 2 has no session {options.ResumeToken}.");

        var session = NewSession(
            options.ResumeToken,
            new OpenCode2SessionContext(options.SessionId, options.OwnerUserId, options.WorkingDirectory, options.ProjectId, options.ProjectName),
            server);
        LogResumed(_logger, session.InstanceId, options.ResumeToken);
        return session;
    }

    /// <inheritdoc />
    public Task<bool> WarmupPooledInstanceAsync(string ownerUserId, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Whether <paramref name="bridgeToken"/> belongs to a server Fleet is running.</summary>
    internal bool IsBridgeToken(string bridgeToken)
    {
        var token = Encoding.UTF8.GetBytes(bridgeToken);
        return _servers.Values.Any(server => server.IsRunning
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

    private OpenCode2HarnessSession NewSession(string harnessSessionId, OpenCode2SessionContext context, OpenCode2Server server)
        => new(
            $"opencode2-{Guid.NewGuid():N}",
            harnessSessionId,
            context,
            server,
            ct => GetServerAsync(context.OwnerUserId, ct),
            _analytics,
            _loggerFactory.CreateLogger<OpenCode2HarnessSession>());

    /// <summary>The owner's running server, started when there's none or the last one stopped.</summary>
    private async Task<OpenCode2Server> GetServerAsync(string ownerUserId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _serversLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_servers.TryGetValue(ownerUserId, out var existing))
            {
                if (existing.IsRunning)
                    return existing;

                _servers.TryRemove(ownerUserId, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var server = await StartServerAsync(ownerUserId, ct).ConfigureAwait(false);
            _servers[ownerUserId] = server;
            return server;
        }
        finally
        {
            _serversLock.Release();
        }
    }

    private async Task<OpenCode2Server> StartServerAsync(string ownerUserId, CancellationToken ct)
    {
        var executable = OpenCode2Executable.TryResolve()
            ?? throw new InvalidOperationException(
                $"OpenCode 2 isn't installed: Fleet couldn't find {OpenCode2Executable.Command} on PATH or in ~/.opencode/bin.");

        // The server would run on the user's own OpenCode data, so make sure it's the API this harness speaks.
        var probe = await HarnessProbe.RunAsync(executable, ["--version"], ct).ConfigureAwait(false);
        var version = HarnessProbe.ParseVersion(probe.StandardOutput);
        if (version is null || !OpenCode2Executable.IsOpenCode2(version))
            throw new InvalidOperationException($"{executable} is OpenCode {version ?? "(unknown version)"}, not OpenCode 2.");

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var bridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        // The agent reaches Fleet under a path that names this process, so Fleet can tell its calls from the user's.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FLEET_BRIDGE_TOKEN"] = bridgeToken,
        };
        if (ResolveLocalFleetUrl() is { } fleetUrl)
            environment["FLEET_URL"] = $"{fleetUrl.TrimEnd('/')}{SessionMessages.AgentPathPrefix}/{bridgeToken}";

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
            return new OpenCode2Server(ownerUserId, client, bridgeToken, process, _loggerFactory.CreateLogger<OpenCode2Server>());
        }
        catch
        {
            client?.Dispose();
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} created as {HarnessSessionId}")]
    private static partial void LogSpawned(ILogger logger, string instanceId, string harnessSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 session {InstanceId} resumed {HarnessSessionId}")]
    private static partial void LogResumed(ILogger logger, string instanceId, string harnessSessionId);
}
