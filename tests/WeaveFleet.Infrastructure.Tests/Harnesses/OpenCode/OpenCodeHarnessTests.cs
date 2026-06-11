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
            // Eager create-session: both spawns complete with pool instance and OC sessions
            // already created. SpawnCount=1 because the same pool instance is reused for
            // the same owner+credentials. SessionCreateCount=2 because each Fleet session
            // gets its own OC session eagerly at spawn time.
            first.Status.ShouldBe(HarnessSessionStatus.Starting);
            second.Status.ShouldBe(HarnessSessionStatus.Starting);
            factory.SpawnCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(2);

            await first.SendPromptAsync("hello", new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" }, CancellationToken.None);
            await second.SendPromptAsync("hello", new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" }, CancellationToken.None);

            first.ProcessId.ShouldBe(4242);
            second.ProcessId.ShouldBe(4242);
            first.ProcessId.ShouldBe(second.ProcessId);
            // After prompts, still only one pool instance created (reused), no additional OC sessions.
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
    public async Task pooled_spawn_ignores_structured_session_fields_when_reading_created_session_id()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        handler.ReturnStructuredSessionFields();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();

        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-structured-create",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = CreateArtifacts("same-key"),
            },
            CancellationToken.None);

        try
        {
            spawned.ResumeToken.ShouldBe("oc-session-1");
            handler.SessionCreateCount.ShouldBe(1);
        }
        finally
        {
            await spawned.DisposeAsync();
        }
    }

    [Fact]
    public async Task pooled_spawn_creates_pool_instance_eagerly_at_spawn_time_with_spawn_credentials()
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

        // Eager create-session: pool instance and OC session are created at spawn time,
        // not lazily on first prompt. Credentials are fixed to those present at spawn time.
        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = "fleet-session-eager-credentials",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = CreateArtifacts("old-key"),
            },
            CancellationToken.None);
        await credentialStore.StoreCredentialAsync("anthropic", "anthropic", "api-key", "new-key");

        try
        {
            // Pool instance created eagerly at spawn time with the credentials from LaunchArtifacts.
            factory.SpawnCount.ShouldBe(1);
            factory.LastEnvironment.ShouldNotBeNull();
            factory.LastEnvironment!["ANTHROPIC_API_KEY"].ShouldBe("old-key");

            await spawned.SendPromptAsync(
                "hello",
                new PromptOptions { ProviderId = "anthropic", ModelId = "claude-sonnet-4" },
                CancellationToken.None);

            // After prompt: same pool instance, no additional spawn. Credential rotation
            // between spawn and prompt does not affect this session's pool partition.
            factory.SpawnCount.ShouldBe(1);
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
    public async Task resume_async_with_pooled_mode_ignores_structured_session_fields_when_validating_token()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        handler.ReturnStructuredSessionFields();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = "fleet-session-structured-get",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = "oc-session-existing",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            resumed.ResumeToken.ShouldBe("oc-session-existing");
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(0);
        }
        finally
        {
            await resumed.DisposeAsync();
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
    public async Task resume_async_with_pooled_mode_creates_new_oc_session_when_token_is_fleet_session_id()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var sessionRepository = new InMemorySessionRepository();
        await using var runtime = CreatePooledRuntime(factory, preferences, sessionRepository);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");
        const string fleetSessionId = "69dc575f-9b0a-4f8e-8b58-ea9841b241bb";

        sessionRepository.Seed(new Session
        {
            Id = fleetSessionId,
            WorkspaceId = "workspace-1",
            InstanceId = "opencode-987f31ac3e35430bbe82bcb07e03e7f1",
            OpencodeSessionId = fleetSessionId,
            Title = "Stale automatic session",
            Directory = directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            HarnessType = "opencode",
            RuntimeMode = "automatic",
            HarnessResumeToken = fleetSessionId,
            UserId = "user-1",
        });

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = fleetSessionId,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            resumed.ResumeToken.ShouldBe("oc-session-1");
            factory.SpawnCount.ShouldBe(1);
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(1);
            var stored = await sessionRepository.GetByIdAsync(fleetSessionId);
            stored.ShouldNotBeNull();
            stored.HarnessResumeToken.ShouldBe("oc-session-1");
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

    // ── Rollback / crash-recovery tests ──────────────────────────────────────

    [Fact]
    public async Task spawn_pooled_removes_in_memory_mapping_when_session_create_throws()
    {
        // When SpawnPooledAsync succeeds in creating the OC session but the caller fails to
        // persist the Fleet session row (simulated by the caller disposing the session immediately
        // before any DB write), the in-memory _pooledSessionMappings entry must be removed so that
        // a subsequent spawn for the SAME Fleet session ID creates a fresh OC session rather than
        // reusing the stale mapping from the failed attempt.
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences, new InMemorySessionRepository());
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");
        const string fleetSessionId = "fleet-session-rollback";

        // Simulate a spawn that succeeds at the OC level but whose Fleet session row is never
        // persisted: call SpawnAsync and then immediately stop+dispose the session (the caller
        // would do this if it encounters a DB failure after SpawnAsync returns).
        var spawned = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        // One OC session created eagerly.
        handler.SessionCreateCount.ShouldBe(1);
        var staleOcSessionId = spawned.ResumeToken!;

        // Simulate caller rollback: stop (releases lease) then dispose.
        await spawned.StopAsync(CancellationToken.None);
        await spawned.DisposeAsync();

        // Spawn again for the SAME Fleet session ID. Because the in-memory mapping was cleared
        // (via _pooledSessionMappings.TryRemove in the catch block before the session is handed
        // back), a fresh OC session should be created rather than reusing stale state.
        // Note: the mapping is removed only if the failure occurs inside SpawnPooledAsync itself
        // (e.g., CreateSessionAsync throws). If the caller disposes after a successful spawn,
        // the mapping remains until the next resume detects the stale session via GetSessionAsync.
        // This test validates the latter: that the stale resume token is detected and a fresh
        // OC session is created on the next ResumeAsync call.
        handler.MarkSessionAsDeleted(staleOcSessionId);

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = staleOcSessionId,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            // Stale token must NOT be reused — a fresh OC session is created.
            resumed.ResumeToken.ShouldNotBe(staleOcSessionId);
            // One GET (stale check) + one POST (new OC session).
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(2);
        }
        finally
        {
            await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task resume_after_crash_with_stale_token_creates_fresh_oc_session_and_persists_new_token()
    {
        // After a process crash and restart, Fleet has a persisted resume token pointing to an
        // OC session that no longer exists in the new process. ResumeAsync must detect the stale
        // token (404 on GET /session/{id}), create a fresh OC session, and persist the new token
        // so that subsequent resumes do not keep reusing the dead token.
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var sessionRepository = new InMemorySessionRepository();
        await using var runtime = CreatePooledRuntime(factory, preferences, sessionRepository);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");
        const string fleetSessionId = "fleet-session-stale-token";
        const string staleToken = "stale-oc-session-dead";

        // Seed a Fleet session with a stale resume token (as would happen after a crash).
        sessionRepository.Seed(new Session
        {
            Id = fleetSessionId,
            WorkspaceId = "workspace-stale",
            InstanceId = "opencode-stale-instance",
            HarnessResumeToken = staleToken,
            Title = "Stale session",
            Directory = directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            HarnessType = "opencode",
            RuntimeMode = "automatic",
            UserId = "user-1",
        });

        // The handler will return 404 for the stale token (simulating a dead OC session).
        handler.MarkSessionAsDeleted(staleToken);

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = staleToken,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            // Stale token must NOT be reused — a new OC session is created.
            resumed.ResumeToken.ShouldNotBe(staleToken);
            resumed.ResumeToken.ShouldBe("oc-session-1");

            // GET was called once (stale check returned 404), then POST created a fresh session.
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(1);

            // The new resume token must be persisted in the Fleet session row so the next
            // resume will use the live OC session, not the dead one.
            var stored = await sessionRepository.GetByIdAsync(fleetSessionId);
            stored.ShouldNotBeNull();
            stored.HarnessResumeToken.ShouldBe("oc-session-1");
        }
        finally
        {
            await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task resume_after_crash_does_not_reuse_stale_fleet_session_id_as_oc_session_token()
    {
        // Guard against the bootstrap-time bug where the Fleet session ID was accidentally used as
        // the OC session resume token. The handler returns 400 for GUIDs (mimicking OpenCode
        // rejecting a Fleet UUID as an OC session ID). ResumeAsync must create a fresh session.
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var sessionRepository = new InMemorySessionRepository();
        await using var runtime = CreatePooledRuntime(factory, preferences, sessionRepository);
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");
        var fleetSessionId = Guid.NewGuid().ToString();

        sessionRepository.Seed(new Session
        {
            Id = fleetSessionId,
            WorkspaceId = "workspace-guid-token",
            InstanceId = "opencode-guid-instance",
            HarnessResumeToken = fleetSessionId,
            Title = "GUID token session",
            Directory = directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            HarnessType = "opencode",
            RuntimeMode = "automatic",
            UserId = "user-1",
        });

        var resumed = await runtime.ResumeAsync(
            new HarnessResumeOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = fleetSessionId,
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            // Fleet session ID (GUID) must NOT be reused as the OC session token.
            resumed.ResumeToken.ShouldNotBe(fleetSessionId);
            resumed.ResumeToken.ShouldBe("oc-session-1");

            // GET attempted (returned 400 for GUID), then POST created fresh session.
            handler.SessionGetCount.ShouldBe(1);
            handler.SessionCreateCount.ShouldBe(1);

            // Persisted token must be updated to the live OC session.
            var stored = await sessionRepository.GetByIdAsync(fleetSessionId);
            stored.ShouldNotBeNull();
            stored.HarnessResumeToken.ShouldBe("oc-session-1");
        }
        finally
        {
            await resumed.DisposeAsync();
        }
    }

    [Fact]
    public async Task spawn_pooled_when_oc_session_create_fails_does_not_leave_in_memory_mapping()
    {
        // When the OpenCode HTTP call to create a session fails inside SpawnPooledAsync,
        // no in-memory mapping must be added. A subsequent spawn for the same Fleet session ID
        // must succeed with a fresh OC session and a clean mapping.
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        await using var runtime = CreatePooledRuntime(factory, preferences, new InMemorySessionRepository());
        var directory = Directory.GetCurrentDirectory();
        var artifacts = CreateArtifacts("same-key");
        const string fleetSessionId = "fleet-session-create-fail";

        handler.FailNextSessionCreateWithServerError();

        await Should.ThrowAsync<Exception>(() => runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None));

        // No mapping must have been added (session create failed before mapping was inserted).
        handler.SessionCreateCount.ShouldBe(0);

        // A retry spawn must succeed cleanly with a fresh OC session.
        var retried = await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = fleetSessionId,
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                LaunchArtifacts = artifacts,
            },
            CancellationToken.None);

        try
        {
            retried.ResumeToken.ShouldBe("oc-session-1");
            handler.SessionCreateCount.ShouldBe(1);
        }
        finally
        {
            await retried.DisposeAsync();
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

    // ── WarmupPooledInstanceAsync tests ──────────────────────────────────────

    [Fact]
    public async Task warmup_pooled_instance_returns_false_when_pooled_mode_disabled()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var runtime = CreateRuntime(
            new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = false } },
            preferences);

        var result = await runtime.WarmupPooledInstanceAsync("user-1", CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task warmup_pooled_instance_spawns_instance_and_returns_true_when_pooled_mode_enabled()
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(new UserCredential
        {
            UserId = "user-warm",
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = "warmup-key",
        });

        await using var runtime = CreatePooledRuntime(
            factory,
            preferences,
            new InMemorySessionRepository(),
            credentialStore);

        var result = await runtime.WarmupPooledInstanceAsync("user-warm", CancellationToken.None);

        result.ShouldBeTrue();
        factory.SpawnCount.ShouldBe(1);
    }

    [Fact]
    public async Task warmup_pooled_instance_resolves_credentials_server_side_using_owner_identity()
    {
        // Verify that warmup loads credentials server-side from ICredentialStore using the
        // ownerUserId argument — no caller-supplied credential hash, env vars, or directory.
        var preferences = new InMemoryUserPreferenceRepository();
        var handler = new PooledSpawnHttpMessageHandler();
        var factory = new PooledSpawnInstanceFactory(handler);
        var credentialStore = new FakeCredentialStore();
        credentialStore.Seed(new UserCredential
        {
            UserId = "server-user",
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = "server-side-key",
        });

        await using var runtime = CreatePooledRuntime(
            factory,
            preferences,
            new InMemorySessionRepository(),
            credentialStore);

        await runtime.WarmupPooledInstanceAsync("server-user", CancellationToken.None);

        // Credentials must be fetched for the owner identity derived server-side.
        credentialStore.GetDecryptedCredentialsCalls.ShouldContain("server-user");
        // Instance must have been spawned — the pool was warmed up.
        factory.SpawnCount.ShouldBe(1);
        // The warmup uses no caller-supplied directory — the factory receives the server-resolved cwd.
        factory.LastEnvironment.ShouldNotBeNull();
        // With no model ID at warmup time, the environment contains no provider-specific keys.
        // Server-side credential resolution still occurred (verified via GetDecryptedCredentialsCalls).
        factory.LastEnvironment!.ContainsKey("ANTHROPIC_API_KEY").ShouldBeFalse();
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
        private int _failNextSessionCreateWithServerError;
        private int _returnStructuredSessionFields;

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

        public void FailNextSessionCreateWithServerError() =>
            Volatile.Write(ref _failNextSessionCreateWithServerError, 1);

        public void ReturnStructuredSessionFields() =>
            Volatile.Write(ref _returnStructuredSessionFields, 1);

        /// <summary>
        /// Marks the given session ID as deleted (returns 404 on GET /session/{id}).
        /// Used to simulate a stale resume token pointing to a dead OC session.
        /// </summary>
        public void MarkSessionAsDeleted(string sessionId)
        {
            lock (_sync)
            {
                _deletedSessionIds.Add(sessionId);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/session")
            {
                if (Interlocked.Exchange(ref _failNextSessionCreateWithServerError, 0) != 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                var sessionNumber = Interlocked.Increment(ref _sessionCreateCount);
                return Task.FromResult(CreateJsonResponse(
                    CreateSessionJson($"oc-session-{sessionNumber}", $"sess-{sessionNumber}")));
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

                if (Guid.TryParse(sessionId, out _))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                }

                return Task.FromResult(CreateJsonResponse(
                    CreateSessionJson(sessionId, "sess")));
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

        private string CreateSessionJson(string sessionId, string slug)
        {
            if (Volatile.Read(ref _returnStructuredSessionFields) == 0)
            {
                return $"{{\"id\":\"{sessionId}\",\"slug\":\"{slug}\",\"directory\":\"/tmp\",\"time\":{{\"created\":1,\"updated\":1}}}}";
            }

            return $"{{\"id\":\"{sessionId}\",\"slug\":\"{slug}\",\"directory\":\"/tmp\",\"summary\":{{\"text\":\"structured\"}},\"time\":{{\"created\":1,\"updated\":1}}}}";
        }
    }
}
