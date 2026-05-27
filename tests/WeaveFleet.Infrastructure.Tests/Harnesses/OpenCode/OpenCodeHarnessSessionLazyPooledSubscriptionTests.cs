using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeHarnessSessionLazyPooledSubscriptionTests
{
    [Fact]
    public async Task subscribe_async_waits_for_lazy_pooled_session_and_yields_routed_events_after_prompt()
    {
        var handler = new RecordingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var pooledInstance = CreateInstance(openCodeHttpClient);
        var lease = pooledInstance.CreateLease(static (releasedLease, _) =>
        {
            releasedLease.Instance.RemoveLease(releasedLease);
            return ValueTask.CompletedTask;
        });
        var bindingTable = new PoolDemuxBindingTable();
        var streamFactory = new FakeStreamFactory();
        await using var demultiplexer = new SseEventDemultiplexer(
            bindingTable,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var handle = new LeasedInstanceHandle(
            _ => Task.FromResult((lease, LeaseGeneration: 1L)),
            (_, _, _) => Task.CompletedTask,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid());
        var session = CreateSession(handle);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<HarnessEvent>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var evt in session.SubscribeAsync(cts.Token))
            {
                events.Add(evt);
                break;
            }
        }, cts.Token);

        await Task.Delay(200, cts.Token);
        consumeTask.IsCompleted.ShouldBeFalse();
        handle.IsAcquired.ShouldBeFalse();
        handler.RequestPaths.ShouldBeEmpty();

        await session.SendPromptAsync("hello", options: null, cts.Token);
        await pooledInstance.WaitForEventSubscriptionAsync("oc-session-1").WaitAsync(TimeSpan.FromSeconds(5));
        var stream = await streamFactory.WaitForSubscriptionAsync(pooledInstance, "/repo/one", 1);
        var routedEvent = CreateSseEvent("message.updated", "oc-session-1");

        await stream.WriteAsync(routedEvent);
        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));

        events.ShouldHaveSingleItem().SessionId.ShouldBe("oc-session-1");
        handler.RequestPaths.ShouldBe([
            "/session?directory=%2Frepo%2Fone",
            "/session/oc-session-1/prompt_async?directory=%2Frepo%2Fone"]);

        await cts.CancelAsync();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task subscribe_async_cancellation_while_waiting_for_lazy_pooled_session_exits_cleanly()
    {
        var handler = new RecordingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        var pooledInstance = CreateInstance(openCodeHttpClient);
        var lease = pooledInstance.CreateLease(static (releasedLease, _) =>
        {
            releasedLease.Instance.RemoveLease(releasedLease);
            return ValueTask.CompletedTask;
        });
        var bindingTable = new PoolDemuxBindingTable();
        await using var demultiplexer = new SseEventDemultiplexer(
            bindingTable,
            new FakeStreamFactory(),
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var handle = new LeasedInstanceHandle(
            _ => Task.FromResult((lease, LeaseGeneration: 1L)),
            (_, _, _) => Task.CompletedTask,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid());
        var session = CreateSession(handle);
        using var cts = new CancellationTokenSource();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var _ in session.SubscribeAsync(cts.Token))
            {
            }
        }, CancellationToken.None);

        await Task.Delay(200);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => consumeTask.WaitAsync(TimeSpan.FromSeconds(5)));
        handle.IsAcquired.ShouldBeFalse();
        handler.RequestPaths.ShouldBeEmpty();

        await session.DisposeAsync();
    }

    private static OpenCodeHarnessSession CreateSession(LeasedInstanceHandle handle)
    {
        var sessionRepo = new InMemorySessionRepository();
        var services = new ServiceCollection();
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IMessageRepository>(new InMemoryMessageRepository());
        services.AddSingleton<IDelegationRepository>(new InMemoryDelegationRepository());
        services.AddSingleton<IEventBroadcaster>(new FakeEventBroadcaster());
        services.AddSingleton<IDbConnectionFactory>(new FakeDbConnectionFactory());
        services.AddSingleton<IOutboxRepository>(new InMemoryOutboxRepository());
        services.AddSingleton<IOutboxDispatcher>(new FakeOutboxDispatcher());
        services.AddSingleton<IUserContext>(new TestUserContext("user-1"));
        services.AddSingleton<DelegationService>();
        var rootProvider = services.BuildServiceProvider();

        return new OpenCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: "fleet-session-1",
            instanceHandle: handle,
            workingDirectory: "/repo/one",
            scopeFactory: rootProvider.GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<OpenCodeHarnessSession>.Instance,
            ownerUserId: "user-1");
    }

    private static PooledOpenCodeInstance CreateInstance(OpenCodeHttpClient openCodeHttpClient)
    {
        return new PooledOpenCodeInstance(
            "key",
            $"instance-{Guid.NewGuid():N}",
            processId: 123,
            openCodeHttpClient,
            processManager: null,
            shutdownAsync: () => ValueTask.CompletedTask);
    }

    private static OpenCodeSseEvent CreateSseEvent(string type, string sessionId)
    {
        var properties = JsonSerializer.SerializeToElement(new { sessionID = sessionId });
        return new OpenCodeSseEvent { Type = type, Properties = properties };
    }

    private sealed class FakeStreamFactory : IOpenCodeSseEventStreamFactory
    {
        private readonly object _sync = new();
        private readonly Dictionary<StreamKey, List<FakeStream>> _streams = new();

        public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeAsync(
            PooledOpenCodeInstance instance,
            string directory,
            Func<Task> connectedAsync,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
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

            try
            {
                await foreach (var evt in stream.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    yield return evt;
                }
            }
            finally
            {
                stream.Cancel();
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

            throw new TimeoutException($"Timed out waiting for subscription {count} for {directory}.");
        }

        private readonly record struct StreamKey(PooledOpenCodeInstance Instance, string Directory);
    }

    private sealed class FakeStream
    {
        private readonly System.Threading.Channels.Channel<OpenCodeSseEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<OpenCodeSseEvent>();

        public ValueTask WriteAsync(OpenCodeSseEvent evt) => _events.Writer.WriteAsync(evt);

        public void Cancel() => _events.Writer.TryComplete();

        public IAsyncEnumerable<OpenCodeSseEvent> ReadAllAsync(CancellationToken ct) => _events.Reader.ReadAllAsync(ct);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);

            var response = request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/session"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"oc-session-1\"}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent);

            return Task.FromResult(response);
        }
    }
}
