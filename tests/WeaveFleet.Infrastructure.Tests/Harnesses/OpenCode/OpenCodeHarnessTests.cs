using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
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

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeHarnessTests
{
    private static OpenCodeHarness CreateHarness() => new();

    private static OpenCodeHarnessRuntime CreateRuntime() =>
        CreateRuntime(new FleetOptions(), new WeaveFleet.Testing.Fakes.Repositories.InMemoryUserPreferenceRepository());

    private static OpenCodeHarnessRuntime CreateRuntime(
        FleetOptions options,
        WeaveFleet.Testing.Fakes.Repositories.InMemoryUserPreferenceRepository preferences) =>
        new(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: options,
            scopeFactory: TestServiceScopeFactory.Create(services => services.AddSingleton<WeaveFleet.Domain.Repositories.IUserPreferenceRepository>(preferences)),
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);

    [Fact]
    public void type_returns_opencode()
    {
        var harness = CreateHarness();

        harness.Type.ShouldBe("opencode");
    }

    [Fact]
    public void display_name_returns_opencode()
    {
        var harness = CreateHarness();

        harness.DisplayName.ShouldBe("OpenCode");
    }

    [Fact]
    public void capabilities_requires_initial_prompt_is_false()
    {
        var harness = CreateHarness();

        harness.Capabilities.RequiresInitialPrompt.ShouldBeFalse();
    }

    [Fact]
    public void capabilities_supports_agents_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsAgents.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_model_selection_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsModelSelection.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_commands_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsCommands.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_forking_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsForking.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_resume_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsResume.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_image_attachments_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsImageAttachments.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_streaming_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void capabilities_supports_delegation_is_true()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsDelegation.ShouldBeTrue();
    }

    [Fact]
    public async Task pooled_mode_defaults_to_off()
    {
        var runtime = CreateRuntime();

        var result = await runtime.IsPooledModeEnabledAsync("user-1", CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task pooled_mode_reads_settings_flag_at_runtime()
    {
        var preferences = new WeaveFleet.Testing.Fakes.Repositories.InMemoryUserPreferenceRepository();
        var runtime = CreateRuntime(
            new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = false } },
            preferences);

        var disabledResult = await runtime.IsPooledModeEnabledAsync("user-1", CancellationToken.None);
        await preferences.SetAsync(OpenCodeFeatureFlagProvider.PooledOpenCodeHarnessPreferenceKey, "true");
        var enabledResult = await runtime.IsPooledModeEnabledAsync("user-1", CancellationToken.None);

        disabledResult.ShouldBeFalse();
        enabledResult.ShouldBeTrue();
    }

    [Fact]
    public async Task spawn_async_with_pooled_mode_reuses_existing_instance_for_same_credentials()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var portAllocator = new PortAllocator(12000, 12000);
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var credentialStore = new FakeCredentialStore();
        await credentialStore.StoreCredentialAsync("anthropic", "anthropic", "api-key", "same-key");
        await using var runtime = CreatePooledRuntime(
            factory,
            preferences,
            new InMemorySessionRepository(),
            credentialStore,
            portAllocator);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = new OpenCodeLaunchArtifacts(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ANTHROPIC_API_KEY"] = "same-key",
            },
            ["anthropic/claude-sonnet-4"]);

        var first = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-1",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        var second = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-2",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            first.Status.ShouldBe(HarnessSessionStatus.Starting);
            second.Status.ShouldBe(HarnessSessionStatus.Starting);
            factory.SpawnCount.ShouldBe(0);
            handler.SessionCreateCount.ShouldBe(0);

            await first.SendPromptAsync("hello", new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" }, CancellationToken.None);
            await second.SendPromptAsync("hello", new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" }, CancellationToken.None);

            first.ProcessId.ShouldBe(4242);
            second.ProcessId.ShouldBe(4242);
            first.ProcessId.ShouldBe(second.ProcessId);
            factory.SpawnCount.ShouldBe(1);
            portAllocator.AllocatedCount.ShouldBe(0);
            handler.SessionCreateCount.ShouldBe(2);
        }
        finally
        {
            await second.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task pooled_spawn_uses_fresh_credentials_on_first_message()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(new UserCredential
        {
            UserId = "user-1",
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = "old-key",
        });
        await using var runtime = CreatePooledRuntime(factory, preferences, new InMemorySessionRepository(), credentialStore);
        var directory = Directory.GetCurrentDirectory();

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-rotated-credentials",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = CreateArtifacts("old-key"),
            },
            CancellationToken.None);
        await credentialStore.StoreCredentialAsync("anthropic", "anthropic", "api-key", "new-key");

        try
        {
            factory.SpawnCount.ShouldBe(0);

            await spawned.SendPromptAsync(
                "hello",
                new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" },
                CancellationToken.None);

            factory.SpawnCount.ShouldBe(1);
            factory.LastEnvironment.ShouldNotBeNull();
            factory.LastEnvironment!["ANTHROPIC_API_KEY"].ShouldBe("new-key");
        }
        finally
        {
            await spawned.DisposeAsync();
        }
    }

    [Fact]
    public async Task resume_async_with_pooled_mode_reattaches_live_session()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-1",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);
        await spawned.SendPromptAsync("hello", null, CancellationToken.None);

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = "fleet-session-1",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = spawned.ResumeToken!,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            resumed.ProcessId.ShouldBe(spawned.ProcessId);
            resumed.ResumeToken.ShouldBe(spawned.ResumeToken);
            factory.SpawnCount.ShouldBe(1);
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(1);
        }
        finally
        {
            await resumed.DisposeAsync();
            await spawned.DisposeAsync();
        }
    }

    [Fact]
    public async Task resume_async_with_pooled_mode_rejects_non_owner_user()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-1",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);
        await spawned.SendPromptAsync("hello", null, CancellationToken.None);

        try
        {
            await Should.ThrowAsync<UnauthorizedAccessException>(() => runtime.ResumeAsync(
                new HarnessResumeOptions
                {
                    SessionId = "fleet-session-1",
                    WorkingDirectory = directory,
                    OwnerUserId = "user-2",
                    ResumeToken = spawned.ResumeToken!,
                    LaunchArtifacts = artifacts,
                },
                CancellationToken.None));

            handler.SessionGetCount.ShouldBe(0);
        }
        finally
        {
            await spawned.DisposeAsync();
        }
    }

    [Fact]
    public async Task resume_async_with_pooled_mode_creates_new_oc_session_when_token_missing()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var sessionRepository = new InMemorySessionRepository();
        await using var runtime = CreatePooledRuntime(factory, preferences, sessionRepository);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = "fleet-session-missing-token",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = "missing-oc-session",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            resumed.ResumeToken.ShouldBe("oc-session-1");
            factory.SpawnCount.ShouldBe(1);
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(1);
        }
        finally
        {
            await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task stop_async_with_pooled_mode_releases_lease_without_killing_shared_process()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-stop",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);
        await spawned.SendPromptAsync("hello", null, CancellationToken.None);

        await spawned.StopAsync(CancellationToken.None);

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = "fleet-session-stop",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = spawned.ResumeToken!,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            factory.SpawnCount.ShouldBe(1);
            factory.ShutdownCount.ShouldBe(0);
            resumed.ProcessId.ShouldBe(spawned.ProcessId);
            resumed.ResumeToken.ShouldBe(spawned.ResumeToken);
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(1);
        }
        finally
        {
            await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task delete_async_with_pooled_mode_deletes_oc_session_and_resume_creates_new_session()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-delete",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);
        await spawned.SendPromptAsync("hello", null, CancellationToken.None);
        var deletedOpenCodeSessionId = spawned.ResumeToken!;

        await spawned.DeleteAsync(CancellationToken.None);

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = "fleet-session-delete",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = deletedOpenCodeSessionId,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            factory.SpawnCount.ShouldBe(1);
            factory.ShutdownCount.ShouldBe(0);
            resumed.ProcessId.ShouldBe(spawned.ProcessId);
            resumed.ResumeToken.ShouldBe("oc-session-2");
            handler.SessionDeleteCount.ShouldBe(1);
            handler.DeletedSessionIds.ShouldContain(deletedOpenCodeSessionId);
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(2);
        }
        finally
        {
            await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task resume_async_with_pooled_mode_reacquires_after_instance_failure()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-crash",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);
        await spawned.SendPromptAsync("hello", null, CancellationToken.None);

        handler.FailNextSessionGetWithServerError();

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = "fleet-session-crash",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = spawned.ResumeToken!,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            factory.SpawnCount.ShouldBe(2);
            resumed.ProcessId.ShouldBe(4243);
            resumed.ResumeToken.ShouldBe(spawned.ResumeToken);
            handler.SessionGetCount.ShouldBe(2);
        }
        finally
        {
            await resumed.DisposeAsync();
            await spawned.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task check_availability_when_binary_missing_returns_not_available()
    {
        // This test relies on "opencode" NOT being on the PATH (expected in CI / dev without OpenCode).
        // If opencode IS installed, the test is skipped.
        var runtime = CreateRuntime();

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        // We can only assert the shape — whether it's available depends on the environment.
        result.ShouldNotBeNull();
        // Either available (binary found) or not (binary missing) — both are valid results.
        if (!result.Available)
        {
            result.Reason.ShouldNotBeNull();
            result.Reason.Contains("opencode", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        }
    }

    // ---------------------------------------------------------------------------
    // Minimal IHttpClientFactory stub (not used in metadata / capability tests)
    // ---------------------------------------------------------------------------

    private sealed class TestHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            new();
    }

    private static OpenCodeHarnessRuntime CreatePooledRuntime(
        PooledSpawnInstanceFactory instanceFactory,
        InMemoryUserPreferenceRepository preferences)
    {
        return CreatePooledRuntime(instanceFactory, preferences, new InMemorySessionRepository());
    }

    private static OpenCodeHarnessRuntime CreatePooledRuntime(
        PooledSpawnInstanceFactory instanceFactory,
        InMemoryUserPreferenceRepository preferences,
        InMemorySessionRepository sessionRepository)
    {
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(new UserCredential
        {
            UserId = "user-1",
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = "same-key",
        });
        return CreatePooledRuntime(instanceFactory, preferences, sessionRepository, credentialStore);
    }

    private static OpenCodeHarnessRuntime CreatePooledRuntime(
        PooledSpawnInstanceFactory instanceFactory,
        InMemoryUserPreferenceRepository preferences,
        InMemorySessionRepository sessionRepository,
        FakeCredentialStore credentialStore)
    {
        return CreatePooledRuntime(
            instanceFactory,
            preferences,
            sessionRepository,
            credentialStore,
            new PortAllocator(10000, 10099));
    }

    private static OpenCodeHarnessRuntime CreatePooledRuntime(
        PooledSpawnInstanceFactory instanceFactory,
        InMemoryUserPreferenceRepository preferences,
        InMemorySessionRepository sessionRepository,
        FakeCredentialStore credentialStore,
        PortAllocator portAllocator)
    {
        return new OpenCodeHarnessRuntime(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: portAllocator,
            options: new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true } },
            scopeFactory: TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IUserPreferenceRepository>(preferences);
                services.AddSingleton<ISessionRepository>(sessionRepository);
                services.AddSingleton<IMessageRepository>(new InMemoryMessageRepository());
                services.AddSingleton<IEventBroadcaster>(new FakeEventBroadcaster());
                services.AddSingleton<ICredentialStore>(credentialStore);
            }),
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            featureFlagProvider: new OpenCodeFeatureFlagProvider(
                new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true } },
                TestServiceScopeFactory.Create(services => services.AddSingleton<IUserPreferenceRepository>(preferences))),
            analyticsCollector: null,
            pooledInstanceFactory: instanceFactory.CreateAsync);
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

    private sealed class PooledSpawnInstanceFactory(PooledSpawnHttpMessageHandler handler)
    {
        private int _spawnCount;
        private int _shutdownCount;
        private IReadOnlyDictionary<string, string>? _lastEnvironment;

        public int SpawnCount => Volatile.Read(ref _spawnCount);

        public int ShutdownCount => Volatile.Read(ref _shutdownCount);

        public IReadOnlyDictionary<string, string>? LastEnvironment => _lastEnvironment;

        public Task<PooledOpenCodeInstance> CreateAsync(string key, string directory, CancellationToken ct)
        {
            return CreateAsync(key, directory, new Dictionary<string, string>(), ct);
        }

        public Task<PooledOpenCodeInstance> CreateAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environmentVariables,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _lastEnvironment = environmentVariables;
            var spawnCount = Interlocked.Increment(ref _spawnCount);
            var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost"),
            };
            var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
            var instance = new PooledOpenCodeInstance(
                key,
                "pooled-instance-1",
                processId: 4241 + spawnCount,
                openCodeHttpClient,
                processManager: null,
                shutdownAsync: () =>
                {
                    Interlocked.Increment(ref _shutdownCount);
                    return ValueTask.CompletedTask;
                });

            return Task.FromResult(instance);
        }
    }

    private sealed class PooledSpawnHttpMessageHandler : HttpMessageHandler
    {
        private readonly HashSet<string> _deletedSessionIds = new(StringComparer.Ordinal);
        private readonly object _sync = new();
        private int _sessionCreateCount;
        private int _sessionGetCount;
        private int _sessionDeleteCount;
        private int _promptCount;
        private int _commandCount;
        private int _failNextSessionGetWithServerError;

        public int SessionCreateCount => Volatile.Read(ref _sessionCreateCount);

        public int SessionGetCount => Volatile.Read(ref _sessionGetCount);

        public int SessionDeleteCount => Volatile.Read(ref _sessionDeleteCount);

        public int PromptCount => Volatile.Read(ref _promptCount);

        public int CommandCount => Volatile.Read(ref _commandCount);

        public IReadOnlyCollection<string> DeletedSessionIds
        {
            get
            {
                lock (_sync)
                {
                    return _deletedSessionIds.ToArray();
                }
            }
        }

        public void FailNextSessionGetWithServerError() =>
            Volatile.Write(ref _failNextSessionGetWithServerError, 1);

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
                if (Interlocked.Exchange(ref _failNextSessionGetWithServerError, 0) != 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                var sessionId = request.RequestUri.AbsolutePath["/session/".Length..];
                if (string.Equals(sessionId, "missing-oc-session", StringComparison.Ordinal) || IsDeleted(sessionId))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(CreateJsonResponse(
                    $"{{\"id\":\"{sessionId}\",\"slug\":\"sess\",\"directory\":\"/tmp\",\"time\":{{\"created\":1,\"updated\":1}}}}"));
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri?.AbsolutePath.StartsWith("/session/", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _sessionDeleteCount);
                var sessionId = request.RequestUri.AbsolutePath["/session/".Length..];
                lock (_sync)
                {
                    _deletedSessionIds.Add(sessionId);
                }

                request.RequestUri.Query.ShouldContain("directory=");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _promptCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/command", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _commandCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private bool IsDeleted(string sessionId)
        {
            lock (_sync)
            {
                return _deletedSessionIds.Contains(sessionId);
            }
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
}
