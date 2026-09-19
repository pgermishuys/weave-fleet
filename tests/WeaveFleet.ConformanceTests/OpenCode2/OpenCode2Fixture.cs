using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FakeLlmServer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.ConformanceTests.OpenCode2;

/// <summary>
/// One real <c>opencode2 serve</c> and <see cref="FakeLlmServerFixture"/> for all of a test class's tests, as Fleet
/// runs one server per owner for every session. The server has a scratch HOME whose config points at the fake model,
/// so it never reads or migrates the user's own OpenCode data. Starting V2 in a fresh HOME takes about 6 s, which is
/// why the tests share it.
/// </summary>
/// <remarks>
/// Finds <c>opencode2</c> where the harness does (<see cref="FindExecutable"/>).
/// </remarks>
public sealed class OpenCode2Host : IAsyncLifetime
{
    internal const string Owner = "test-user";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"conformance-oc2-{Guid.NewGuid():N}");

    internal FakeLlmServerFixture Llm { get; private set; } = null!;

    internal OpenCode2Server Server { get; private set; } = null!;

    /// <summary>
    /// The <c>opencode2</c> the harness would start, found the way it finds it (<see cref="OpenCode2Install.Locate"/>:
    /// the remembered mode's install, else a separate one under <c>~/.weave/harnesses/opencode2</c>, else PATH and
    /// <c>~/.opencode/bin</c>). Reads only; it doesn't remember a mode.
    /// </summary>
    internal static string? FindExecutable()
        => new OpenCode2Install(
                ExecutableResolver.HomeDirectory() ?? Environment.CurrentDirectory,
                Environment.GetEnvironmentVariable,
                ExecutableResolver.UserBinDirectories(),
                (path, ct) => HarnessProbe.CheckInstalledAsync("OpenCode 2", OpenCode2Executable.Command, path, NullLogger.Instance, ct),
                OperatingSystem.IsWindows())
            .Locate()?.ExecutablePath;

    /// <summary>Whether a 2.x <c>opencode2</c> is installed where the harness looks for it.</summary>
    public static bool IsAvailable()
    {
        if (FindExecutable() is not { } executable)
            return false;

        try
        {
            var probe = HarnessProbe.RunAsync(executable, ["--version"], CancellationToken.None).GetAwaiter().GetResult();
            return HarnessProbe.ParseVersion(probe.StandardOutput) is { } version && OpenCode2Executable.IsOpenCode2(version);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Starts nothing when <c>opencode2</c> isn't installed: the tests skip or fail on their own.</remarks>
    public async ValueTask InitializeAsync()
    {
        if (FindExecutable() is not { } executable)
            return;

        Llm = await FakeLlmServerFixture.StartAsync();

        // Title generation offers no tools; answer it without using up a test's scripted responses.
        Llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Conformance" };

        var loggerFactory = NullLoggerFactory.Instance;
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var process = new OpenCode2ProcessManager(loggerFactory.CreateLogger<OpenCode2ProcessManager>());
        var environment = OpenCode2ScratchHome.Write(_root, Llm.BaseUrl);
        var baseUrl = await process.StartAsync(
            new OpenCode2ProcessOptions
            {
                ExecutablePath = executable,
                WorkingDirectory = environment["HOME"],
                Password = password,
                EnvironmentVariables = environment,
                StartupTimeout = TimeSpan.FromSeconds(60),
            },
            CancellationToken.None);

        var client = new OpenCode2HttpClient(
            NewHttpClient(baseUrl, password, TimeSpan.FromSeconds(30)),
            NewHttpClient(baseUrl, password, Timeout.InfiniteTimeSpan),
            loggerFactory.CreateLogger<OpenCode2HttpClient>());
        Server = new OpenCode2Server(Owner, client, bridgeToken: "conformance", process, loggerFactory.CreateLogger<OpenCode2Server>());
        await Server.WaitForEventsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Server is not null)
            await Server.DisposeAsync();
        if (Llm is not null)
            await Llm.DisposeAsync();

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static HttpClient NewHttpClient(Uri baseUrl, string password, TimeSpan timeout)
    {
        var client = new HttpClient { BaseAddress = baseUrl, Timeout = timeout };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}")));
        return client;
    }
}

/// <summary>
/// <see cref="IHarnessSessionFixture"/> for <see cref="OpenCode2HarnessSession"/>: a new V2 session on the shared
/// <see cref="OpenCode2Host"/>'s server for each test.
/// </summary>
public sealed class OpenCode2Fixture(OpenCode2Host host) : IHarnessSessionFixture
{
    private OpenCode2HarnessSession? _session;

    /// <inheritdoc />
    public async Task<IHarnessSession> CreateSessionAsync(string workingDirectory, CancellationToken ct = default)
    {
        var server = host.Server;
        var info = await server.Client.CreateSessionAsync(workingDirectory, ct);
        _session = new OpenCode2HarnessSession(
            $"opencode2-test-{Guid.NewGuid():N}",
            info,
            new OpenCode2SessionContext($"fleet-test-{Guid.NewGuid():N}", OpenCode2Host.Owner, workingDirectory, ProjectId: null, ProjectName: null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            delegations: null,
            NullLoggerFactory.Instance.CreateLogger<OpenCode2HarnessSession>());
        return _session;
    }

    /// <inheritdoc />
    public void EnqueueResponse(ScriptedLlmResponse response) => host.Llm.Queue.Enqueue(response);

    /// <inheritdoc />
    /// <remarks>
    /// Waits for the session's turn to end and drops what it didn't use, so the next test's session starts with the
    /// model's queue to itself.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        var client = host.Server.Client;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        try
        {
            while ((await client.GetActiveSessionIdsAsync(CancellationToken.None)).Contains(_session.ResumeToken)
                   && DateTime.UtcNow < deadline)
            {
                await client.InterruptAsync(_session.ResumeToken, CancellationToken.None);
                await Task.Delay(200);
            }
        }
        catch (HttpRequestException)
        {
            // The server is gone; nothing is running on it.
        }

        while (host.Llm.Queue.TryDequeue(out _))
        {
        }

        await _session.DisposeAsync();
    }
}

/// <summary>A scratch HOME for OpenCode 2 whose config points at the fake model.</summary>
internal static class OpenCode2ScratchHome
{
    /// <summary>Writes the folders and config under <paramref name="root"/>; returns the variables that point V2 at them.</summary>
    public static Dictionary<string, string> Write(string root, Uri llmBaseUrl)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        foreach (var folder in environment.Values)
            Directory.CreateDirectory(folder);

        var configFolder = Path.Combine(environment["XDG_CONFIG_HOME"], "opencode");
        Directory.CreateDirectory(configFolder);
        File.WriteAllText(Path.Combine(configFolder, "opencode.json"), $$"""
            {
              "model": "fake/fake-model",
              "small_model": "fake/fake-model",
              "provider": {
                "fake": {
                  "npm": "@ai-sdk/openai-compatible",
                  "options": { "baseURL": "{{llmBaseUrl.ToString().TrimEnd('/')}}/v1", "apiKey": "fake-key" },
                  "models": { "fake-model": { "name": "Fake", "tool_call": true } }
                }
              }
            }
            """);
        return environment;
    }
}
