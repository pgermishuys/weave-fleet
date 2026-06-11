using System.Net.Http.Headers;
using System.Security.Cryptography;
using FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace NuCode.ConformanceTests.OpenCode;

/// <summary>
/// <see cref="IHarnessSessionFixture"/> for <see cref="OpenCodeHarnessSession"/>.
/// Starts a <see cref="FakeLlmServerFixture"/> (fake OpenAI-compatible API) and
/// spawns a real <c>opencode</c> process pointed at it, so tests run without real LLM costs.
/// </summary>
/// <remarks>
/// Requires the <c>opencode</c> binary to be on PATH.
/// Tests using this fixture are skipped automatically when opencode is not available.
/// </remarks>
public sealed class OpenCodeFixture : IHarnessSessionFixture
{
    private FakeLlmServerFixture? _fakeLlm;
    private OpenCodeProcessManager? _processManager;
    private OpenCodeHarnessSession? _session;

    /// <summary>
    /// Returns true if the opencode binary is available on PATH.
    /// Used to skip tests when opencode is not installed.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "opencode",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IHarnessSession> CreateSessionAsync(string workingDirectory, CancellationToken ct = default)
    {
        // Start the fake LLM server
        _fakeLlm = await FakeLlmServerFixture.StartAsync();

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string username = "opencode";

        var loggerFactory = NullLoggerFactory.Instance;
        _processManager = new OpenCodeProcessManager(
            loggerFactory.CreateLogger<OpenCodeProcessManager>());

        var processInfo = await _processManager.StartAsync(
            new OpenCodeProcessOptions
            {
                Port = 0,
                Hostname = "127.0.0.1",
                WorkingDirectory = workingDirectory,
                Password = password,
                Username = username,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    // Point opencode at the fake LLM server
                    ["OPENAI_API_KEY"] = "fake-key",
                    ["OPENAI_BASE_URL"] = _fakeLlm.BaseUrl.ToString(),
                    ["ANTHROPIC_API_KEY"] = "fake-key",
                    ["ANTHROPIC_BASE_URL"] = _fakeLlm.BaseUrl.ToString(),
                },
                StartupTimeout = TimeSpan.FromSeconds(30),
            },
            ct);

        var httpClient = new HttpClient
        {
            BaseAddress = processInfo.BaseUrl,
        };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));

        var openCodeHttpClient = new OpenCodeHttpClient(
            httpClient,
            loggerFactory.CreateLogger<OpenCodeHttpClient>());

        // Health check with retries
        Exception? lastEx = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await openCodeHttpClient.CheckHealthAsync(ct);
                lastEx = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
                await Task.Delay(500, ct);
            }
        }

        if (lastEx is not null)
        {
            throw new InvalidOperationException(
                "OpenCode process started but health check failed after 5 attempts.", lastEx);
        }

        var portAllocator = new PortAllocator(40000, 41000);
        var instanceId = $"opencode-test-{Guid.NewGuid():N}";
        var fleetSessionId = $"fleet-test-{Guid.NewGuid():N}";

        _session = new OpenCodeHarnessSession(
            instanceId: instanceId,
            fleetSessionId: fleetSessionId,
            instanceHandle: new OwnedInstanceHandle(
                openCodeHttpClient,
                _processManager,
                portAllocator,
                processInfo.Port,
                workingDirectory,
                TimeSpan.FromSeconds(10)),
            workingDirectory: workingDirectory,
            scopeFactory: new NoOpServiceScopeFactory(),
            logger: loggerFactory.CreateLogger<OpenCodeHarnessSession>(),
            ownerUserId: "test-user");

        return _session;
    }

    /// <inheritdoc />
    public void EnqueueResponse(ScriptedLlmResponse response)
    {
        if (_fakeLlm is null)
            throw new InvalidOperationException("Fixture not initialized. Call CreateSessionAsync first.");
        _fakeLlm.Queue.Enqueue(response);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        if (_fakeLlm is not null)
        {
            await _fakeLlm.DisposeAsync();
        }
    }

    private sealed class NoOpServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NoOpServiceScope();

        private sealed class NoOpServiceScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new NoOpServiceProvider();
            public void Dispose() { }
        }

        private sealed class NoOpServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
