using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode.Pooling;

public sealed class SecurityAuditTests
{
    [Fact]
    public async Task sse_event_without_parseable_session_id_is_dropped_never_forwarded_and_increments_metric()
    {
        var instance = CreateInstance();
        var resolver = new StaticBindingResolver();
        var streamFactory = new FakeStreamFactory();
        using var listener = new SseDropMetricListener();
        listener.Start();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var consumerId = Guid.NewGuid();
        var consumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            consumer,
            CancellationToken.None);
        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);

        await stream.WriteAsync(CreateEventWithNumericSessionId());

        await WaitForDroppedCountAsync(demultiplexer, 1);
        await listener.WaitForCounterAtLeastAsync(1);
        demultiplexer.DroppedUnattributableEventCount.ShouldBe(1);
        await AssertNoEventAsync(consumer);
    }

    [Fact]
    public async Task sse_event_without_bound_session_id_is_dropped_never_forwarded_and_increments_metric()
    {
        var instance = CreateInstance();
        var resolver = new StaticBindingResolver();
        var streamFactory = new FakeStreamFactory();
        using var listener = new SseDropMetricListener();
        listener.Start();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var consumerId = Guid.NewGuid();
        var consumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            consumer,
            CancellationToken.None);
        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);

        await stream.WriteAsync(CreateEvent("message.updated", "oc-session-unbound"));

        await WaitForDroppedCountAsync(demultiplexer, 1);
        await listener.WaitForCounterAtLeastAsync(1);
        demultiplexer.DroppedUnattributableEventCount.ShouldBe(1);
        await AssertNoEventAsync(consumer);
    }

    [Fact]
    public async Task double_lease_release_is_idempotent_and_does_not_underflow_ref_count()
    {
        var factory = new RegistryInstanceFactory();
        await using var registry = CreateRegistry(factory, TimeSpan.FromMilliseconds(100));

        var firstLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var secondLease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var instance = firstLease.Instance;

        await firstLease.DisposeAsync();
        await firstLease.DisposeAsync();

        (await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromMilliseconds(250))).ShouldBeFalse();

        await secondLease.DisposeAsync();

        (await factory.WaitForShutdownAsync(instance.InstanceId, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        factory.ShutdownCount.ShouldBe(1);
    }

    [Fact]
    public async Task credential_rotation_mid_session_lazy_acquire_uses_new_credentials_and_does_not_reuse_old_pool()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledHttpMessageHandler();
        var factory = new PooledRuntimeInstanceFactory(handler);
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(CreateCredential("old-key"));
        await using var runtime = CreatePooledRuntime(factory, preferences, credentialStore);
        var directory = Directory.GetCurrentDirectory();
        var oldArtifacts = CreateArtifacts("old-key");

        var oldSession = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-old-pool",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = oldArtifacts,
            },
            CancellationToken.None);
        var lazySession = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-rotated-pool",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = oldArtifacts,
            },
            CancellationToken.None);

        try
        {
            await oldSession.SendPromptAsync(
                "use old credentials",
                new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" },
                CancellationToken.None);

            await credentialStore.StoreCredentialAsync("anthropic", "anthropic", "api-key", "new-key");

            await lazySession.SendPromptAsync(
                "use rotated credentials",
                new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" },
                CancellationToken.None);

            factory.SpawnCount.ShouldBe(2);
            lazySession.ProcessId.ShouldNotBe(oldSession.ProcessId);
            factory.Environments.Count.ShouldBe(2);
            factory.Environments[0]["ANTHROPIC_API_KEY"].ShouldBe("old-key");
            factory.Environments[1]["ANTHROPIC_API_KEY"].ShouldBe("new-key");
        }
        finally
        {
            await lazySession.DisposeAsync();
            await oldSession.DisposeAsync();
        }
    }

    [Fact]
    public async Task concurrent_stop_and_crash_race_does_not_deadlock_and_session_reaches_terminal_state()
    {
        var factory = new RegistryInstanceFactory(useHttpClient: true);
        await using var registry = CreateRegistry(factory, TimeSpan.FromMinutes(1));
        var lease = await registry.AcquireAsync("credential-hash", CancellationToken.None);
        var handle = new LeasedInstanceHandle(
            lease,
            new SseEventDemultiplexer(
                new StaticBindingResolver(),
                new FakeStreamFactory(),
                NullLogger<SseEventDemultiplexer>.Instance,
                TimeSpan.Zero,
                TimeSpan.Zero),
            new PoolDemuxBindingTable(),
            Directory.GetCurrentDirectory(),
            "fleet-session-race",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 1);
        await using var session = new OpenCodeHarnessSession(
            instanceId: "opencode-race",
            fleetSessionId: "fleet-session-race",
            instanceHandle: handle,
            workingDirectory: Directory.GetCurrentDirectory(),
            scopeFactory: CreateScopeFactory(new InMemoryUserPreferenceRepository(), new FakeCredentialStore()),
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stopTask = Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        });
        var crashTask = Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            await lease.Instance.ReportCrashAsync(new InvalidOperationException("process crashed during stop")).ConfigureAwait(false);
        });

        start.SetResult();

        await Task.WhenAll(stopTask, crashTask).WaitAsync(TimeSpan.FromSeconds(5));
        session.Status.ShouldBe(HarnessSessionStatus.Stopped);
    }

    [Fact]
    public async Task resume_by_non_owner_user_is_rejected_before_contacting_pooled_backend()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledHttpMessageHandler();
        var factory = new PooledRuntimeInstanceFactory(handler);
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(CreateCredential("same-key"));
        await using var runtime = CreatePooledRuntime(factory, preferences, credentialStore);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var ownerSession = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-owned",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);
        await ownerSession.SendPromptAsync("bind session", null, CancellationToken.None);

        try
        {
            await Should.ThrowAsync<UnauthorizedAccessException>(() => runtime.ResumeAsync(
                new HarnessResumeOptions
                {
                    SessionId = "fleet-session-owned",
                    WorkingDirectory = directory,
                    OwnerUserId = "user-2",
                    ResumeToken = ownerSession.ResumeToken!,
                    LaunchArtifacts = artifacts,
                },
                CancellationToken.None));

            handler.SessionGetCount.ShouldBe(0);
        }
        finally
        {
            await ownerSession.DisposeAsync();
        }
    }

    [Fact]
    public async Task stale_binding_generation_event_is_dropped_and_not_forwarded()
    {
        var instance = CreateInstance();
        var consumerId = Guid.NewGuid();
        var table = new PoolDemuxBindingTable();
        var streamFactory = new FakeStreamFactory();
        await using var demultiplexer = new SseEventDemultiplexer(
            table,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var consumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            leaseGeneration: 2,
            consumer,
            CancellationToken.None);
        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);

        table.Bind(instance, "oc-session-stale", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 1);
        await stream.WriteAsync(CreateEvent("message.updated", "oc-session-stale"));

        await WaitForDroppedCountAsync(demultiplexer, 1);
        demultiplexer.DroppedUnattributableEventCount.ShouldBe(1);
        await AssertNoEventAsync(consumer);
    }

    private static PooledOpenCodeInstanceRegistry CreateRegistry(RegistryInstanceFactory factory, TimeSpan idleTtl)
    {
        return new PooledOpenCodeInstanceRegistry(
            (key, ct) => factory.CreateAsync(key, ct),
            idleTtl,
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);
    }

    private static OpenCodeHarnessRuntime CreatePooledRuntime(
        PooledRuntimeInstanceFactory instanceFactory,
        InMemoryUserPreferenceRepository preferences,
        FakeCredentialStore credentialStore)
    {
        return new OpenCodeHarnessRuntime(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true } },
            scopeFactory: CreateScopeFactory(preferences, credentialStore),
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            featureFlagProvider: new OpenCodeFeatureFlagProvider(
                new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true } },
                TestServiceScopeFactory.Create(services => services.AddSingleton<IUserPreferenceRepository>(preferences))),
            analyticsCollector: null,
            pooledInstanceFactory: instanceFactory.CreateAsync);
    }

    private static IServiceScopeFactory CreateScopeFactory(
        InMemoryUserPreferenceRepository preferences,
        FakeCredentialStore credentialStore)
    {
        return TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IUserPreferenceRepository>(preferences);
            services.AddSingleton<ISessionRepository>(new InMemorySessionRepository());
            services.AddSingleton<IMessageRepository>(new InMemoryMessageRepository());
            services.AddSingleton<IEventBroadcaster>(new FakeEventBroadcaster());
            services.AddSingleton<ICredentialStore>(credentialStore);
        });
    }

    private static UserCredential CreateCredential(string apiKey)
    {
        return new UserCredential
        {
            UserId = "user-1",
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = apiKey,
        };
    }

    private static OpenCodeLaunchArtifacts CreateArtifacts(string apiKey)
    {
        return new OpenCodeLaunchArtifacts(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ANTHROPIC_API_KEY"] = apiKey,
            },
            ["anthropic/claude-sonnet-4"]);
    }

    private static PooledOpenCodeInstance CreateInstance()
    {
        return new PooledOpenCodeInstance(
            "key",
            $"instance-{Guid.NewGuid():N}",
            processId: 123,
            shutdownAsync: () => ValueTask.CompletedTask);
    }

    private static OpenCodeSseEvent CreateEvent(string type, string sessionId)
    {
        var properties = JsonSerializer.SerializeToElement(new { sessionID = sessionId });
        return new OpenCodeSseEvent { Type = type, Properties = properties };
    }

    private static OpenCodeSseEvent CreateEventWithNumericSessionId()
    {
        var properties = JsonSerializer.SerializeToElement(new { sessionID = 12345 });
        return new OpenCodeSseEvent { Type = "message.updated", Properties = properties };
    }

    private static async Task AssertNoEventAsync(Channel<OpenCodeSseEvent> channel)
    {
        try
        {
            var evt = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(150));
            throw new InvalidOperationException($"Unexpected event received: {evt.Type}.");
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task WaitForDroppedCountAsync(SseEventDemultiplexer demultiplexer, long count)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (demultiplexer.DroppedUnattributableEventCount >= count)
            {
                return;
            }

            await Task.Delay(10);
        }

        demultiplexer.DroppedUnattributableEventCount.ShouldBe(count);
    }

    private static OpenCodeHttpClient CreateOpenCodeHttpClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost"),
        };

        return new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
    }

    private sealed class StaticBindingResolver : IOpenCodeSseEventBindingResolver
    {
        public bool TryResolveConsumer(
            PooledOpenCodeInstance instance,
            string directory,
            string openCodeSessionId,
            out Guid consumerId,
            out long leaseGeneration)
        {
            consumerId = Guid.Empty;
            leaseGeneration = 0;
            return false;
        }
    }

    private sealed class FakeStreamFactory : IOpenCodeSseEventStreamFactory
    {
        private readonly object _sync = new();
        private readonly Dictionary<StreamKey, List<FakeStream>> _streams = new();

        public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeAsync(
            PooledOpenCodeInstance instance,
            string directory,
            Func<Task> connectedAsync,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var stream = new FakeStream();
            lock (_sync)
            {
                var key = new StreamKey(instance, directory);
                if (!_streams.TryGetValue(key, out var streams))
                {
                    streams = [];
                    _streams[key] = streams;
                }

                streams.Add(stream);
            }

            await connectedAsync().ConfigureAwait(false);

            await foreach (var evt in stream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }

        public async Task<FakeStream> WaitForSubscriptionAsync(PooledOpenCodeInstance instance, string directory, int count)
        {
            var key = new StreamKey(instance, directory);
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                lock (_sync)
                {
                    if (_streams.TryGetValue(key, out var streams) && streams.Count >= count)
                    {
                        return streams[count - 1];
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The SSE stream subscription was not created.");
        }

        private readonly record struct StreamKey(PooledOpenCodeInstance Instance, string Directory);
    }

    private sealed class FakeStream
    {
        private readonly Channel<OpenCodeSseEvent> _channel = Channel.CreateUnbounded<OpenCodeSseEvent>();

        public ValueTask WriteAsync(OpenCodeSseEvent evt)
        {
            return _channel.Writer.WriteAsync(evt);
        }

        public IAsyncEnumerable<OpenCodeSseEvent> ReadAllAsync(CancellationToken ct)
        {
            return _channel.Reader.ReadAllAsync(ct);
        }
    }

    private sealed class RegistryInstanceFactory
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _shutdowns = new(StringComparer.Ordinal);
        private readonly bool _useHttpClient;
        private int _spawnCount;
        private int _shutdownCount;

        public RegistryInstanceFactory()
        {
        }

        public RegistryInstanceFactory(bool useHttpClient)
        {
            _useHttpClient = useHttpClient;
        }

        public int ShutdownCount => Volatile.Read(ref _shutdownCount);

        public Task<PooledOpenCodeInstance> CreateAsync(string key, CancellationToken ct)
        {
            return CreateAsync(key, Directory.GetCurrentDirectory(), new Dictionary<string, string>(), ct);
        }

        public Task<PooledOpenCodeInstance> CreateAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = directory;
            _ = environment;
            var number = Interlocked.Increment(ref _spawnCount);
            var instanceId = $"registry-instance-{number}";
            var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdowns[instanceId] = shutdown;
            var httpClient = _useHttpClient ? CreateOpenCodeHttpClient(new PooledHttpMessageHandler()) : null;

            return Task.FromResult(new PooledOpenCodeInstance(
                key,
                instanceId,
                processId: 5000 + number,
                httpClient,
                processManager: null,
                shutdownAsync: () =>
                {
                    Interlocked.Increment(ref _shutdownCount);
                    shutdown.TrySetResult();
                    return ValueTask.CompletedTask;
                }));
        }

        public async Task<bool> WaitForShutdownAsync(string instanceId, TimeSpan timeout)
        {
            if (!_shutdowns.TryGetValue(instanceId, out var shutdown))
            {
                return false;
            }

            try
            {
                await shutdown.Task.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class PooledRuntimeInstanceFactory(PooledHttpMessageHandler handler)
    {
        private readonly object _sync = new();
        private readonly List<IReadOnlyDictionary<string, string>> _environments = [];
        private int _spawnCount;

        public int SpawnCount => Volatile.Read(ref _spawnCount);

        public IReadOnlyList<IReadOnlyDictionary<string, string>> Environments
        {
            get
            {
                lock (_sync)
                {
                    return [.. _environments];
                }
            }
        }

        public Task<PooledOpenCodeInstance> CreateAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environmentVariables,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = directory;
            lock (_sync)
            {
                _environments.Add(new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal));
            }

            var spawnCount = Interlocked.Increment(ref _spawnCount);
            var instance = new PooledOpenCodeInstance(
                key,
                $"pooled-runtime-instance-{spawnCount}",
                processId: 6000 + spawnCount,
                CreateOpenCodeHttpClient(handler),
                processManager: null,
                shutdownAsync: () => ValueTask.CompletedTask);

            return Task.FromResult(instance);
        }
    }

    private sealed class PooledHttpMessageHandler : HttpMessageHandler
    {
        private int _sessionCreateCount;
        private int _sessionGetCount;

        public int SessionGetCount => Volatile.Read(ref _sessionGetCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/session")
            {
                var sessionNumber = Interlocked.Increment(ref _sessionCreateCount);
                return Task.FromResult(CreateJsonResponse(
                    $"{{\"id\":\"oc-session-{sessionNumber}\",\"slug\":\"sess-{sessionNumber}\",\"directory\":\"/tmp\",\"time\":{{\"created\":1,\"updated\":1}}}}"));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.StartsWith("/session/", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _sessionGetCount);
                var sessionId = request.RequestUri.AbsolutePath["/session/".Length..];
                return Task.FromResult(CreateJsonResponse(
                    $"{{\"id\":\"{sessionId}\",\"slug\":\"sess\",\"directory\":\"/tmp\",\"time\":{{\"created\":1,\"updated\":1}}}}"));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static HttpResponseMessage CreateJsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
                },
            };
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            _ = name;
            return new HttpClient();
        }
    }

    private sealed class SseDropMetricListener : IDisposable
    {
        private const string CounterName = "weave_fleet.opencode.sse.unattributable_events.dropped";
        private readonly MeterListener _listener = new();
        private long _counterValue;

        public SseDropMetricListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == FleetInstrumentation.ServiceName
                    && instrument.Name == CounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            {
                Interlocked.Add(ref _counterValue, measurement);
            });
        }

        public void Start()
        {
            _listener.Start();
        }

        public async Task WaitForCounterAtLeastAsync(long count)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                if (Volatile.Read(ref _counterValue) >= count)
                {
                    return;
                }

                await Task.Delay(10);
            }

            Volatile.Read(ref _counterValue).ShouldBeGreaterThanOrEqualTo(count);
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
