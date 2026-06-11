using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

public sealed class NonPooledRegressionTests
{
    [Fact]
    [Trait("Category", "HarnessSmoke")]
    public async Task feature_flag_off_keeps_per_session_process_isolation()
    {
        var pooledFactory = new RecordingPooledInstanceFactory();
        await using var runtime = CreateRuntime(pooledFactory);
        var directory = CreateDirectory();

        var first = await runtime.SpawnAsync(CreateSpawnOptions("fleet-a", directory), CancellationToken.None);
        var second = await runtime.SpawnAsync(CreateSpawnOptions("fleet-b", directory), CancellationToken.None);

        try
        {
            first.ProcessId.ShouldNotBeNull();
            second.ProcessId.ShouldNotBeNull();
            first.ProcessId.ShouldNotBe(second.ProcessId);
            first.ResumeToken.ShouldBeNull();
            second.ResumeToken.ShouldBeNull();
            pooledFactory.SpawnCount.ShouldBe(0);
            runtime.GetPooledOpenCodePoolHealth().Instances.ShouldBeEmpty();
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "HarnessSmoke")]
    public async Task feature_flag_off_uses_five_processes_for_five_sessions_under_load()
    {
        var pooledFactory = new RecordingPooledInstanceFactory();
        await using var runtime = CreateRuntime(pooledFactory);
        var directory = CreateDirectory();
        var sessions = new List<IHarnessSession>();

        try
        {
            for (var i = 0; i < 5; i++)
            {
                sessions.Add(await runtime.SpawnAsync(CreateSpawnOptions($"fleet-{i}", directory), CancellationToken.None));
            }

            sessions.Select(session => session.ProcessId).ShouldAllBe(processId => processId.HasValue);
            sessions.Select(session => session.ProcessId).Distinct().Count().ShouldBe(5);
            pooledFactory.SpawnCount.ShouldBe(0);
            runtime.GetPooledOpenCodePoolHealth().Instances.ShouldBeEmpty();
        }
        finally
        {
            await DisposeSessionsAsync(sessions.ToArray());
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OpenCodeHarnessRuntime CreateRuntime(RecordingPooledInstanceFactory pooledFactory)
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(CreateCredential("user-1", "same-key"));
        var options = new FleetOptions
        {
            Harness = new HarnessOptions
            {
                PooledOpenCodeHarness = false,
                PooledOpenCodeIdleTtlSeconds = 60,
            },
        };
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IUserPreferenceRepository>(preferences);
            services.AddSingleton<ISessionRepository>(new InMemorySessionRepository());
            services.AddSingleton<IMessageRepository>(new InMemoryMessageRepository());
            services.AddSingleton<IEventBroadcaster>(new FakeEventBroadcaster());
            services.AddSingleton<ICredentialStore>(credentialStore);
        });

        return new OpenCodeHarnessRuntime(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: options,
            scopeFactory: scopeFactory,
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            featureFlagProvider: new OpenCodeFeatureFlagProvider(options, scopeFactory),
            analyticsCollector: null,
            pooledInstanceFactory: pooledFactory.CreateAsync);
    }

    private static HarnessSpawnOptions CreateSpawnOptions(string sessionId, string directory) =>
        new()
        {
            SessionId = sessionId,
            WorkingDirectory = directory,
            OwnerUserId = "user-1",
            LaunchArtifacts = CreateArtifacts("same-key"),
        };

    private static UserCredential CreateCredential(string userId, string apiKey) =>
        new()
        {
            UserId = userId,
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = apiKey,
        };

    private static OpenCodeLaunchArtifacts CreateArtifacts(string apiKey)
    {
        return new OpenCodeLaunchArtifacts(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ANTHROPIC_API_KEY"] = apiKey,
            },
            ["anthropic/claude-sonnet-4"]);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"non-pooled-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task DisposeSessionsAsync(params IHarnessSession[] sessions)
    {
        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingPooledInstanceFactory
    {
        private int _spawnCount;
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _environments = new();

        public int SpawnCount => Volatile.Read(ref _spawnCount);

        public IReadOnlyCollection<IReadOnlyDictionary<string, string>> Environments => _environments.ToArray();

        public Task<PooledOpenCodeInstance> CreateAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environmentVariables,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var spawnCount = Interlocked.Increment(ref _spawnCount);
            _environments.Enqueue(new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal));
            throw new InvalidOperationException($"Pooled OpenCode factory must not be called when feature flag is off. Spawn count: {spawnCount}; key: {key}; directory: {directory}.");
        }
    }
}
