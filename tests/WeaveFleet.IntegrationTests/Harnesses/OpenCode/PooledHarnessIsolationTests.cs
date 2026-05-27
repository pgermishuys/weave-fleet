using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

public sealed class PooledHarnessIsolationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task same_user_same_directory_share_process_but_events_are_isolated()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();

        var first = await SpawnAsync(runtime, "fleet-a", "user-1", directory, "same-key");
        var second = await SpawnAsync(runtime, "fleet-b", "user-1", directory, "same-key");

        try
        {
            await first.SendPromptAsync("first", CreatePromptOptions(), CancellationToken.None);
            await second.SendPromptAsync("second", CreatePromptOptions(), CancellationToken.None);

            first.ProcessId.ShouldBe(second.ProcessId);
            factory.SpawnCount.ShouldBe(1);

            await AssertEventsAreIsolatedAsync(first, second, handler, "event-a", "event-b");
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task five_same_credential_sessions_share_one_pooled_process_under_load()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();
        var sessions = new List<IHarnessSession>();

        try
        {
            for (var i = 0; i < 5; i++)
            {
                sessions.Add(await SpawnAsync(runtime, $"fleet-{i}", "user-1", directory, "same-key"));
            }

            await Task.WhenAll(sessions.Select((session, index) =>
                session.SendPromptAsync($"prompt-{index}", CreatePromptOptions(), CancellationToken.None)));

            sessions.Select(session => session.ProcessId).Distinct().Count().ShouldBe(1);
            factory.SpawnCount.ShouldBe(1);

            var health = runtime.GetPooledOpenCodePoolHealth();
            health.InstanceCount.ShouldBe(1);
            health.SessionCount.ShouldBe(5);
        }
        finally
        {
            await DisposeSessionsAsync(sessions.ToArray());
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task same_user_different_directories_share_process_but_events_are_isolated()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var root = CreateDirectory();
        var firstDirectory = Path.Combine(root, "dir-a");
        var secondDirectory = Path.Combine(root, "dir-b");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        var first = await SpawnAsync(runtime, "fleet-a", "user-1", firstDirectory, "same-key");
        var second = await SpawnAsync(runtime, "fleet-b", "user-1", secondDirectory, "same-key");

        try
        {
            await first.SendPromptAsync("first", CreatePromptOptions(), CancellationToken.None);
            await second.SendPromptAsync("second", CreatePromptOptions(), CancellationToken.None);

            first.ProcessId.ShouldBe(second.ProcessId);
            factory.SpawnCount.ShouldBe(1);
            await AssertEventsAreIsolatedAsync(first, second, handler, "event-a", "event-b");
            handler.EventStreamDirectories.ShouldBe([firstDirectory, secondDirectory], ignoreOrder: true);
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task different_credentials_use_separate_processes()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory, credentials: CreateCredentials(("user-1", "key-1"), ("user-2", "key-2")));
        var directory = CreateDirectory();

        var first = await SpawnAsync(runtime, "fleet-a", "user-1", directory, "key-1");
        var second = await SpawnAsync(runtime, "fleet-b", "user-2", directory, "key-2");

        try
        {
            await first.SendPromptAsync("first", CreatePromptOptions(), CancellationToken.None);
            await second.SendPromptAsync("second", CreatePromptOptions(), CancellationToken.None);

            first.ProcessId.ShouldNotBe(second.ProcessId);
            factory.SpawnCount.ShouldBe(2);
            factory.Environments.ShouldContain(env => env["ANTHROPIC_API_KEY"] == "key-1");
            factory.Environments.ShouldContain(env => env["ANTHROPIC_API_KEY"] == "key-2");
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task stopped_session_can_resume_on_shared_process()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        var sessions = new InMemorySessionRepository();
        await using var runtime = CreateRuntime(factory, sessionRepository: sessions);
        var directory = CreateDirectory();
        var spawned = await SpawnAsync(runtime, "fleet-stop", "user-1", directory, "same-key");

        try
        {
            await spawned.SendPromptAsync("start", CreatePromptOptions(), CancellationToken.None);
            var originalProcessId = spawned.ProcessId;
            var resumeToken = spawned.ResumeToken!;

            await spawned.StopAsync(CancellationToken.None);
            var resumed = await runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = "fleet-stop",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = resumeToken,
                LaunchArtifacts = CreateArtifacts("same-key"),
            }, CancellationToken.None);

            try
            {
                resumed.ProcessId.ShouldBe(originalProcessId);
                resumed.ResumeToken.ShouldBe(resumeToken);
                factory.SpawnCount.ShouldBe(1);
                handler.SessionGetCount.ShouldBe(1);
            }
            finally
            {
                await resumed.DisposeAsync();
            }
        }
        finally
        {
            await spawned.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task process_crash_recovers_with_replacement_process_and_keeps_session_routes_isolated()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();
        var first = await SpawnAsync(runtime, "fleet-a", "user-1", directory, "same-key");
        var second = await SpawnAsync(runtime, "fleet-b", "user-1", directory, "same-key");

        try
        {
            await first.SendPromptAsync("first", CreatePromptOptions(), CancellationToken.None);
            await second.SendPromptAsync("second", CreatePromptOptions(), CancellationToken.None);
            var originalProcessId = first.ProcessId;
            var firstResumeToken = first.ResumeToken!;
            var secondResumeToken = second.ResumeToken!;

            handler.FailNextSessionGetWithServerError();
            var recoveredFirst = await runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = "fleet-a",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = firstResumeToken,
                LaunchArtifacts = CreateArtifacts("same-key"),
            }, CancellationToken.None);
            var recoveredSecond = await runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = "fleet-b",
                WorkingDirectory = directory,
                OwnerUserId = "user-1",
                ResumeToken = secondResumeToken,
                LaunchArtifacts = CreateArtifacts("same-key"),
            }, CancellationToken.None);

            recoveredFirst.ProcessId.ShouldNotBe(originalProcessId);
            recoveredSecond.ProcessId.ShouldBe(recoveredFirst.ProcessId);
            factory.SpawnCount.ShouldBe(2);

            await AssertEventsAreIsolatedAsync(recoveredFirst, recoveredSecond, handler, "after-crash-a", "after-crash-b");

            await DisposeSessionsAsync(recoveredFirst, recoveredSecond);
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task process_crash_recovers_all_active_sessions_within_five_seconds()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();
        var sessions = new List<IHarnessSession>();
        var recoveredSessions = Array.Empty<IHarnessSession>();

        try
        {
            for (var i = 0; i < 5; i++)
            {
                sessions.Add(await SpawnAsync(runtime, $"fleet-recover-{i}", "user-1", directory, "same-key"));
            }

            await Task.WhenAll(sessions.Select((session, index) =>
                session.SendPromptAsync($"before-crash-{index}", CreatePromptOptions(), CancellationToken.None)));
            sessions.Select(session => session.ProcessId).Distinct().Count().ShouldBe(1);
            var originalProcessId = sessions[0].ProcessId;
            originalProcessId.ShouldNotBeNull();
            var crashedInstance = factory.LastInstance;
            crashedInstance.ShouldNotBeNull();

            var recoveryStarted = DateTimeOffset.UtcNow;
            recoveredSessions = await RecoverAllSessionsAfterCrashAsync(runtime, crashedInstance, sessions, directory)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var recoveryElapsed = DateTimeOffset.UtcNow - recoveryStarted;

            recoveryElapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            recoveredSessions.Select(session => session.ProcessId).Distinct().Count().ShouldBe(1);
            recoveredSessions.ShouldAllBe(session => session.ProcessId != originalProcessId);
            factory.SpawnCount.ShouldBe(2);
            await AssertEventsAreIsolatedAsync(recoveredSessions[0], recoveredSessions[1], handler, "recovered-a", "recovered-b");
        }
        finally
        {
            await DisposeSessionsAsync(recoveredSessions);
            await DisposeSessionsAsync(sessions.ToArray());
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task slash_commands_route_to_the_bound_opencode_session()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();
        var first = await SpawnAsync(runtime, "fleet-a", "user-1", directory, "same-key");
        var second = await SpawnAsync(runtime, "fleet-b", "user-1", directory, "same-key");

        try
        {
            await first.SendPromptAsync("first", CreatePromptOptions(), CancellationToken.None);
            await second.SendPromptAsync("second", CreatePromptOptions(), CancellationToken.None);

            await first.SendCommandAsync(CreateCommandOptions("init", "first"), CancellationToken.None);
            await second.SendCommandAsync(CreateCommandOptions("review", "second"), CancellationToken.None);

            handler.CommandRequests.Length.ShouldBe(2);
            handler.CommandRequests.ShouldContain(command => command.SessionId == first.ResumeToken && command.Command == "init" && command.Arguments == "first");
            handler.CommandRequests.ShouldContain(command => command.SessionId == second.ResumeToken && command.Command == "review" && command.Arguments == "second");
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task concurrent_message_sends_do_not_cross_contaminate_sessions()
    {
        var handler = new IsolationHttpMessageHandler();
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();
        var first = await SpawnAsync(runtime, "fleet-a", "user-1", directory, "same-key");
        var second = await SpawnAsync(runtime, "fleet-b", "user-1", directory, "same-key");

        try
        {
            var firstSend = first.SendPromptAsync("alpha", CreatePromptOptions(), CancellationToken.None);
            var secondSend = second.SendPromptAsync("beta", CreatePromptOptions(), CancellationToken.None);

            await Task.WhenAll(firstSend, secondSend);

            first.ProcessId.ShouldBe(second.ProcessId);
            factory.SpawnCount.ShouldBe(1);
            handler.PromptRequests.Length.ShouldBe(2);
            handler.PromptRequests.ShouldContain(prompt => prompt.SessionId == first.ResumeToken && prompt.Text == "alpha");
            handler.PromptRequests.ShouldContain(prompt => prompt.SessionId == second.ResumeToken && prompt.Text == "beta");

            await AssertEventsAreIsolatedAsync(first, second, handler, "concurrent-a", "concurrent-b");
        }
        finally
        {
            await DisposeSessionsAsync(first, second);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task first_message_events_are_not_dropped_when_sse_subscription_starts_slowly()
    {
        var handler = new IsolationHttpMessageHandler { DelayEventStreamReadUntilPrompt = true };
        var factory = new IsolationInstanceFactory(handler);
        await using var runtime = CreateRuntime(factory);
        var directory = CreateDirectory();
        var session = await SpawnAsync(runtime, "fleet-first-message", "user-1", directory, "same-key");

        try
        {
            var sendTask = session.SendPromptAsync("first", CreatePromptOptions(), CancellationToken.None);

            await handler.WaitForEventSubscribersAsync(expectedCount: 1, CancellationToken.None);
            handler.PromptRequests.Length.ShouldBe(0);
            handler.ReleaseEventStreamRead();

            await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
            using var collector = EventCollector.Start(session);
            var firstMessageEvent = await collector.WaitForEventAsync("first-message-event");

            firstMessageEvent.SessionId.ShouldBe(session.ResumeToken);
            handler.PromptRequests.Length.ShouldBe(1);
        }
        finally
        {
            await DisposeSessionsAsync(session);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertEventsAreIsolatedAsync(
        IHarnessSession first,
        IHarnessSession second,
        IsolationHttpMessageHandler handler,
        string firstMarker,
        string secondMarker)
    {
        using var firstCollector = EventCollector.Start(first);
        using var secondCollector = EventCollector.Start(second);

        await handler.WaitForEventSubscribersAsync(expectedCount: 1, CancellationToken.None);
        Task PublishMarkersAsync()
        {
            handler.PublishEvent(first.ResumeToken!, firstMarker);
            handler.PublishEvent(second.ResumeToken!, secondMarker);
            return Task.CompletedTask;
        }

        await PublishMarkersAsync();
        var firstEventTask = firstCollector.WaitForEventAsync(firstMarker, PublishMarkersAsync);
        var secondEventTask = secondCollector.WaitForEventAsync(secondMarker, PublishMarkersAsync);
        var firstEvent = await firstEventTask;
        var secondEvent = await secondEventTask;

        firstEvent.SessionId.ShouldBe(first.ResumeToken);
        secondEvent.SessionId.ShouldBe(second.ResumeToken);

        await Task.Delay(TimeSpan.FromMilliseconds(150));

        firstCollector.HasEvent(secondMarker).ShouldBeFalse();
        secondCollector.HasEvent(firstMarker).ShouldBeFalse();
    }

    private static async Task<IHarnessSession[]> RecoverAllSessionsAfterCrashAsync(
        OpenCodeHarnessRuntime runtime,
        PooledOpenCodeInstance crashedInstance,
        IReadOnlyList<IHarnessSession> sessions,
        string directory)
    {
        var resumeTokens = sessions.Select(session => session.ResumeToken).ToArray();
        resumeTokens.ShouldAllBe(token => !string.IsNullOrWhiteSpace(token));

        await crashedInstance.ReportCrashAsync(new InvalidOperationException("pooled process killed"));

        return await Task.WhenAll(sessions.Select((session, index) => runtime.ResumeAsync(new HarnessResumeOptions
        {
            SessionId = $"fleet-recover-{index}",
            WorkingDirectory = directory,
            OwnerUserId = "user-1",
            ResumeToken = resumeTokens[index]!,
            LaunchArtifacts = CreateArtifacts("same-key"),
        }, CancellationToken.None)));
    }

    private static async Task<IHarnessSession> SpawnAsync(
        OpenCodeHarnessRuntime runtime,
        string sessionId,
        string ownerUserId,
        string directory,
        string apiKey)
    {
        return await runtime.SpawnAsync(new HarnessSpawnOptions
        {
            SessionId = sessionId,
            WorkingDirectory = directory,
            OwnerUserId = ownerUserId,
            LaunchArtifacts = CreateArtifacts(apiKey),
        }, CancellationToken.None);
    }

    private static OpenCodeHarnessRuntime CreateRuntime(
        IsolationInstanceFactory factory,
        IReadOnlyCollection<UserCredential>? credentials = null,
        InMemorySessionRepository? sessionRepository = null)
    {
        var preferences = new InMemoryUserPreferenceRepository();
        var credentialStore = new FakeCredentialStore();
        foreach (var credential in credentials ?? CreateCredentials(("user-1", "same-key")))
        {
            credentialStore.Seed(credential);
        }

        return new OpenCodeHarnessRuntime(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: new FleetOptions { Harness = new HarnessOptions { PooledOpenCodeHarness = true, PooledOpenCodeIdleTtlSeconds = 60 } },
            scopeFactory: TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IUserPreferenceRepository>(preferences);
                services.AddSingleton<ISessionRepository>(sessionRepository ?? new InMemorySessionRepository());
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
            pooledInstanceFactory: factory.CreateAsync);
    }

    private static UserCredential[] CreateCredentials(params (string UserId, string ApiKey)[] credentials) =>
        credentials.Select(item => new UserCredential
        {
            UserId = item.UserId,
            Namespace = "anthropic",
            Kind = "api-key",
            Label = "anthropic",
            EncryptedValue = item.ApiKey,
        }).ToArray();

    private static OpenCodeLaunchArtifacts CreateArtifacts(string apiKey)
    {
        return new OpenCodeLaunchArtifacts(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ANTHROPIC_API_KEY"] = apiKey,
            },
            ["anthropic/claude-sonnet-4"]);
    }

    private static PromptOptions CreatePromptOptions() =>
        new() { ProviderId = "anthropic", ModelId = "claude-sonnet-4" };

    private static CommandOptions CreateCommandOptions(string command, string arguments) =>
        new()
        {
            Command = command,
            Arguments = arguments,
            ProviderId = "anthropic",
            ModelId = "claude-sonnet-4",
        };

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pooled-harness-isolation-{Guid.NewGuid():N}");
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

    private sealed class IsolationInstanceFactory(IsolationHttpMessageHandler handler)
    {
        private int _spawnCount;
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _environments = new();

        public int SpawnCount => Volatile.Read(ref _spawnCount);

        public IReadOnlyCollection<IReadOnlyDictionary<string, string>> Environments => _environments.ToArray();

        public PooledOpenCodeInstance? LastInstance { get; private set; }

        public Task<PooledOpenCodeInstance> CreateAsync(
            string key,
            string directory,
            IReadOnlyDictionary<string, string> environmentVariables,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var spawnCount = Interlocked.Increment(ref _spawnCount);
            _environments.Enqueue(new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal));

            var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost"),
            };
            var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
            var instance = new PooledOpenCodeInstance(
                key,
                $"pooled-instance-{spawnCount}",
                7000 + spawnCount,
                openCodeHttpClient,
                processManager: null,
                shutdownAsync: () => ValueTask.CompletedTask);

            LastInstance = instance;
            return Task.FromResult(instance);
        }
    }

    private sealed class IsolationHttpMessageHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, EventStream> _eventStreams = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<PromptRequest> _promptRequests = new();
        private readonly ConcurrentQueue<CommandRequest> _commandRequests = new();
        private readonly TaskCompletionSource _eventStreamReadReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sessionCreateCount;
        private int _sessionGetCount;
        private int _failNextSessionGetWithServerError;

        public bool DelayEventStreamReadUntilPrompt { get; init; }

        public int SessionGetCount => Volatile.Read(ref _sessionGetCount);

        public PromptRequest[] PromptRequests => _promptRequests.ToArray();

        public CommandRequest[] CommandRequests => _commandRequests.ToArray();

        public IReadOnlyCollection<string> EventStreamDirectories => _eventStreams.Keys.ToArray();

        public void FailNextSessionGetWithServerError() =>
            Volatile.Write(ref _failNextSessionGetWithServerError, 1);

        public void PublishEvent(string sessionId, string marker)
        {
            var json = JsonSerializer.Serialize(new
            {
                type = EventTypes.MessageCreated,
                properties = new
                {
                    sessionID = sessionId,
                    marker,
                    info = new
                    {
                        id = $"message-{marker}",
                        sessionID = sessionId,
                        role = "user",
                    },
                },
            });
            foreach (var stream in _eventStreams.Values)
            {
                stream.Publish(json);
            }
        }

        public void ReleaseEventStreamRead() => _eventStreamReadReleased.TrySetResult();

        public async Task WaitForEventSubscribersAsync(int expectedCount, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            while (!cts.IsCancellationRequested)
            {
                if (_eventStreams.Values.Sum(stream => stream.SubscriberCount) >= expectedCount)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cts.Token);
            }

            throw new TimeoutException("Timed out waiting for pooled SSE subscribers.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                var directory = ReadDirectory(request.RequestUri);
                var stream = _eventStreams.GetOrAdd(directory, static _ => new EventStream());
                if (DelayEventStreamReadUntilPrompt)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = stream.CreateContent(_eventStreamReadReleased.Task, cancellationToken),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = stream.CreateContent(cancellationToken),
                };
            }

            if (request.Method == HttpMethod.Post && path == "/session")
            {
                var sessionNumber = Interlocked.Increment(ref _sessionCreateCount);
                return CreateJsonResponse(
                    $"{{\"id\":\"oc-session-{sessionNumber}\",\"slug\":\"session-{sessionNumber}\",\"directory\":\"{EncodeJsonString(ReadDirectory(request.RequestUri))}\",\"time\":{{\"created\":1,\"updated\":1}}}}");
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/session/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _sessionGetCount);
                if (Interlocked.Exchange(ref _failNextSessionGetWithServerError, 0) != 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var sessionId = path["/session/".Length..];
                return CreateJsonResponse(
                    $"{{\"id\":\"{EncodeJsonString(sessionId)}\",\"slug\":\"session\",\"directory\":\"{EncodeJsonString(ReadDirectory(request.RequestUri))}\",\"time\":{{\"created\":1,\"updated\":1}}}}");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/prompt_async", StringComparison.Ordinal))
            {
                var sessionId = ReadSessionId(path, "/prompt_async");
                var body = await ReadBodyAsync(request, cancellationToken);
                _promptRequests.Enqueue(new PromptRequest(sessionId, ReadFirstPromptText(body), ReadDirectory(request.RequestUri)));
                if (DelayEventStreamReadUntilPrompt)
                {
                    PublishEvent(sessionId, "first-message-event");
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/command", StringComparison.Ordinal))
            {
                var sessionId = ReadSessionId(path, "/command");
                var body = await ReadBodyAsync(request, cancellationToken);
                using var document = JsonDocument.Parse(body);
                _commandRequests.Enqueue(new CommandRequest(
                    sessionId,
                    document.RootElement.GetProperty("command").GetString()!,
                    document.RootElement.GetProperty("arguments").GetString()!,
                    ReadDirectory(request.RequestUri)));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static string ReadSessionId(string path, string suffix)
        {
            var value = path["/session/".Length..^suffix.Length];
            return Uri.UnescapeDataString(value);
        }

        private static string ReadDirectory(Uri? uri)
        {
            if (uri is null)
            {
                return string.Empty;
            }

            const string prefix = "?directory=";
            var query = uri.Query;
            if (!query.StartsWith(prefix, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var value = query[prefix.Length..];
            var ampersandIndex = value.IndexOf('&', StringComparison.Ordinal);
            return Uri.UnescapeDataString(ampersandIndex >= 0 ? value[..ampersandIndex] : value);
        }

        private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Content.ShouldNotBeNull();
            return await request.Content.ReadAsStringAsync(ct);
        }

        private static string ReadFirstPromptText(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
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

        private static string EncodeJsonString(string value) =>
            JsonSerializer.Serialize(value)[1..^1];
    }

    private sealed record PromptRequest(string SessionId, string Text, string Directory);

    private sealed record CommandRequest(string SessionId, string Command, string Arguments, string Directory);

    private sealed class EventStream
    {
        private readonly ConcurrentDictionary<Guid, BlockingCollection<string>> _subscribers = new();

        public int SubscriberCount => _subscribers.Count;

        public void Publish(string json)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Add($"data: {json}\n\n");
            }
        }

        public HttpContent CreateContent(CancellationToken ct) =>
            new EventStreamContent(this, Task.CompletedTask, ct);

        public HttpContent CreateContent(Task releaseRead, CancellationToken ct) =>
            new EventStreamContent(this, releaseRead, ct);

        private sealed class EventStreamContent(EventStream owner, Task releaseRead, CancellationToken ct) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                await using var eventStream = CreateSubscribedStream();
                await eventStream.CopyToAsync(stream, ct).ConfigureAwait(false);
            }

            protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
                CreateSubscribedStream();

            protected override Task<Stream> CreateContentReadStreamAsync() =>
                Task.FromResult<Stream>(CreateSubscribedStream());

            protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
                Task.FromResult<Stream>(CreateSubscribedStream());

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }

            private SseStream CreateSubscribedStream()
            {
                var subscriberId = Guid.NewGuid();
                var queue = new BlockingCollection<string>();
                owner._subscribers[subscriberId] = queue;
                return new SseStream(owner, subscriberId, queue, releaseRead, ct);
            }
        }

        private sealed class SseStream(
            EventStream owner,
            Guid subscriberId,
            BlockingCollection<string> queue,
            Task releaseRead,
            CancellationToken ct) : Stream
        {
            private byte[] _current = [];
            private int _offset;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!EnsureCurrentFrame())
                {
                    return 0;
                }

                var bytesToCopy = Math.Min(count, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset, bytesToCopy);
                _offset += bytesToCopy;
                return bytesToCopy;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                if (!EnsureCurrentFrame(linkedCts.Token))
                {
                    return ValueTask.FromResult(0);
                }

                var bytesToCopy = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, bytesToCopy).CopyTo(buffer);
                _offset += bytesToCopy;
                return ValueTask.FromResult(bytesToCopy);
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    owner._subscribers.TryRemove(subscriberId, out _);
                    queue.Dispose();
                }

                base.Dispose(disposing);
            }

            private bool EnsureCurrentFrame()
            {
                return EnsureCurrentFrame(ct);
            }

            private bool EnsureCurrentFrame(CancellationToken cancellationToken)
            {
                if (_offset < _current.Length)
                {
                    return true;
                }

                try
                {
                    releaseRead.Wait(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                try
                {
                    var frame = queue.Take(cancellationToken);
                    _current = Encoding.UTF8.GetBytes(frame);
                    _offset = 0;
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    private sealed class EventCollector : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<HarnessEvent> _events = new();
        private readonly Task _task;

        private EventCollector(IHarnessSession session)
        {
            _task = Task.Run(async () =>
            {
                await foreach (var evt in session.SubscribeAsync(_cts.Token))
                {
                    _events.Enqueue(evt);
                }
            });
        }

        public static EventCollector Start(IHarnessSession session) => new(session);

        public async Task<HarnessEvent> WaitForEventAsync(string marker)
        {
            return await WaitForEventAsync(marker, static () => Task.CompletedTask);
        }

        public async Task<HarnessEvent> WaitForEventAsync(string marker, Func<Task> onPollAsync)
        {
            ArgumentNullException.ThrowIfNull(onPollAsync);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!cts.IsCancellationRequested)
            {
                var match = _events.FirstOrDefault(evt => HasMarker(evt, marker));
                if (match is not null)
                {
                    return match;
                }

                await onPollAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(25), cts.Token);
            }

            throw new TimeoutException($"Timed out waiting for marker '{marker}'.");
        }

        public bool HasEvent(string marker) => _events.Any(evt => HasMarker(evt, marker));

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _task.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(IsCancellationException))
            {
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private static bool HasMarker(HarnessEvent evt, string marker)
        {
            return evt.Payload.HasValue
                && evt.Payload.Value.ValueKind == JsonValueKind.Object
                && evt.Payload.Value.TryGetProperty("marker", out var markerElement)
                && string.Equals(markerElement.GetString(), marker, StringComparison.Ordinal);
        }

        private static bool IsCancellationException(Exception ex) =>
            ex is OperationCanceledException;
    }
}
