using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode.Pooling;

public sealed class LeasedInstanceHandleCommandRoutingTests
{
    [Fact]
    public async Task send_command_rejects_unbound_opencode_session_id_before_http()
    {
        var handler = new RecordingHttpMessageHandler();
        var leasedHandle = CreateLeasedHandle(handler, out var bindingTable, out var instance);
        bindingTable.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 7);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            leasedHandle.SendCommandAsync("oc-session-2", CreateCommandRequest(), CancellationToken.None));

        exception.Message.ShouldContain("pooled session binding");
        handler.RequestPaths.ShouldBeEmpty();
        await leasedHandle.DisposeAsync();
    }

    [Fact]
    public async Task send_command_routes_bound_backend_opencode_session_id_to_http()
    {
        var handler = new RecordingHttpMessageHandler();
        var leasedHandle = CreateLeasedHandle(handler, out _, out _);
        await leasedHandle.BindSessionAsync("oc-session-1", CancellationToken.None);

        await leasedHandle.SendCommandAsync("oc-session-1", CreateCommandRequest(), CancellationToken.None);

        handler.RequestPaths.ShouldHaveSingleItem()
            .ShouldBe("/session/oc-session-1/command?directory=%2Frepo%2Fone");
        handler.RequestBodies.ShouldHaveSingleItem()
            .ShouldContain("\"command\":\"test\"");
        await leasedHandle.DisposeAsync();
    }

    [Fact]
    public async Task bind_session_without_callback_returns_binding()
    {
        var handler = new RecordingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var instance = CreateInstance(new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance));
        var lease = instance.CreateLease(static (releasedLease, _) =>
        {
            releasedLease.Instance.RemoveLease(releasedLease);
            return ValueTask.CompletedTask;
        });
        var bindingTable = new PoolDemuxBindingTable();
        var demultiplexer = new SseEventDemultiplexer(bindingTable, NullLogger<SseEventDemultiplexer>.Instance);
        var handle = new LeasedInstanceHandle(
            lease,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 7);

        var binding = await handle.BindSessionAsync("oc-session-1", CancellationToken.None);

        binding.Instance.ShouldBeSameAs(instance);
        binding.LeaseGeneration.ShouldBe(7);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task http_request_rejects_wrong_directory_before_http()
    {
        var handler = new RecordingHttpMessageHandler();
        var leasedHandle = CreateLeasedHandle(handler, out _, out _);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            leasedHandle.HttpClient.GetAgentsAsync("/repo/two", CancellationToken.None));

        exception.Message.ShouldContain("request directory");
        handler.RequestPaths.ShouldBeEmpty();
        await leasedHandle.DisposeAsync();
    }

    [Fact]
    public async Task http_request_allows_configured_directory()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.ResponseContent = "[]";
        var leasedHandle = CreateLeasedHandle(handler, out _, out _);

        await leasedHandle.HttpClient.GetAgentsAsync("/repo/one", CancellationToken.None);

        handler.RequestPaths.ShouldHaveSingleItem()
            .ShouldBe("/agent?directory=%2Frepo%2Fone");
        await leasedHandle.DisposeAsync();
    }

    [Fact]
    public async Task lazy_handle_acquires_once_and_rejects_acquire_after_dispose()
    {
        var handler = new RecordingHttpMessageHandler { ResponseContent = "[]" };
        var acquireCount = 0;
        var releaseCount = 0;
        var instance = CreateInstance(handler);
        var lease = instance.CreateLease((releasedLease, _) =>
        {
            releasedLease.Instance.RemoveLease(releasedLease);
            releaseCount++;
            return ValueTask.CompletedTask;
        });
        var handle = new LeasedInstanceHandle(
            _ =>
            {
                acquireCount++;
                return Task.FromResult((lease, LeaseGeneration: 5L));
            },
            (_, _, _) => Task.CompletedTask,
            new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance),
            new PoolDemuxBindingTable(),
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid());

        handle.IsAcquired.ShouldBeFalse();
        await handle.EnsureConnectedAsync(CancellationToken.None);
        await handle.EnsureConnectedAsync(CancellationToken.None);

        handle.IsAcquired.ShouldBeTrue();
        handle.IsRunning.ShouldBeTrue();
        handle.ProcessId.ShouldBe(123);
        acquireCount.ShouldBe(1);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        releaseCount.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(() => handle.EnsureConnectedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task lazy_handle_rejects_negative_generation_and_missing_http_client()
    {
        var bindingTable = new PoolDemuxBindingTable();
        var demultiplexer = new SseEventDemultiplexer(bindingTable, NullLogger<SseEventDemultiplexer>.Instance);
        var handleWithNegativeGeneration = new LeasedInstanceHandle(
            _ => Task.FromResult((CreateLeaseWithoutHttpClient(), LeaseGeneration: -1L)),
            (_, _, _) => Task.CompletedTask,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid());

        var generationException = await Should.ThrowAsync<InvalidOperationException>(() =>
            handleWithNegativeGeneration.EnsureConnectedAsync(CancellationToken.None));
        generationException.Message.ShouldContain("Lease generation");

        var handleWithoutHttpClient = new LeasedInstanceHandle(
            _ => Task.FromResult((CreateLeaseWithoutHttpClient(), LeaseGeneration: 1L)),
            (_, _, _) => Task.CompletedTask,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid());

        var httpClientException = await Should.ThrowAsync<InvalidOperationException>(() =>
            handleWithoutHttpClient.EnsureConnectedAsync(CancellationToken.None));
        httpClientException.Message.ShouldContain("HTTP client");
    }

    [Fact]
    public async Task lazy_handle_rejects_missing_acquire_delegate()
    {
        var handler = new RecordingHttpMessageHandler();
        var leaseWithoutAcquireDelegate = CreateInstance(handler).CreateLease(static (lease, _) =>
        {
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
        var handle = new LeasedInstanceHandle(
            leaseWithoutAcquireDelegate,
            new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance),
            new PoolDemuxBindingTable(),
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 1);

        typeof(LeasedInstanceHandle)
            .GetField("_lease", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull()
            .SetValue(handle, null);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => handle.EnsureConnectedAsync(CancellationToken.None));
        exception.Message.ShouldContain("lease is not available");
    }

    [Fact]
    public async Task lazy_handle_disposed_before_acquire_releases_nothing_and_properties_reflect_unacquired_state()
    {
        var acquireCount = 0;
        var handle = new LeasedInstanceHandle(
            _ =>
            {
                acquireCount++;
                return Task.FromResult((CreateLeaseWithoutHttpClient(), LeaseGeneration: 1L));
            },
            (_, _, _) => Task.CompletedTask,
            new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance),
            new PoolDemuxBindingTable(),
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid());

        handle.ProcessId.ShouldBeNull();
        handle.IsRunning.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => _ = handle.HttpClient).Message.ShouldContain("not been acquired");

        await handle.DisposeAsync();

        acquireCount.ShouldBe(0);
    }

    [Fact]
    public async Task lazy_handle_bind_and_command_before_acquire_callback_is_configured_are_rejected()
    {
        var instance = CreateInstance(new RecordingHttpMessageHandler());
        var lease = instance.CreateLease(static (releasedLease, _) =>
        {
            releasedLease.Instance.RemoveLease(releasedLease);
            return ValueTask.CompletedTask;
        });
        var handle = new LeasedInstanceHandle(
            lease,
            new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance),
            new PoolDemuxBindingTable(),
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 1);
        var acquiredField = typeof(LeasedInstanceHandle).GetField("_lease", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _lease field.");
        acquiredField.SetValue(handle, null);

        await Should.ThrowAsync<InvalidOperationException>(() => handle.BindSessionAsync("oc-session-1", CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => handle.SendCommandAsync("oc-session-1", CreateCommandRequest(), CancellationToken.None));

        await lease.DisposeAsync();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task crash_reconnects_to_replacement_and_increments_generation_for_binding()
    {
        var firstHandler = new RecordingHttpMessageHandler { ResponseContent = "[]" };
        var secondHandler = new RecordingHttpMessageHandler { ResponseContent = "[]" };
        var bindingTable = new PoolDemuxBindingTable();
        var demultiplexer = new SseEventDemultiplexer(bindingTable, NullLogger<SseEventDemultiplexer>.Instance);
        var firstInstance = CreateInstance(firstHandler);
        var secondInstance = CreateInstance(secondHandler);
        var firstLease = firstInstance.CreateLease(static (lease, _) =>
        {
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
        var secondLease = secondInstance.CreateLease(static (lease, _) =>
        {
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
        var handle = new LeasedInstanceHandle(
            firstLease,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 7);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        handle.ProcessExited += (_, exitCode) => exited.TrySetResult(exitCode);

        firstLease.NotifyFaulted(new InvalidOperationException("process crashed"));
        firstLease.NotifyReplaced(secondLease);

        (await exited.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(-1);
        handle.IsRunning.ShouldBeFalse();

        await handle.EnsureConnectedAsync(CancellationToken.None);
        var binding = await handle.BindSessionAsync("oc-session-1", CancellationToken.None);

        binding.Instance.ShouldBeSameAs(secondInstance);
        binding.LeaseGeneration.ShouldBe(8);
        handle.IsRunning.ShouldBeTrue();

        await handle.DisposeAsync();
    }

    [Fact]
    public async Task immediate_dispose_core_releases_current_replacement_after_reconnect()
    {
        var firstHandler = new RecordingHttpMessageHandler { ResponseContent = "[]" };
        var secondHandler = new RecordingHttpMessageHandler { ResponseContent = "[]" };
        var firstInstance = CreateInstance(firstHandler);
        var secondInstance = CreateInstance(secondHandler);
        InstanceLeaseReleaseMode? firstReleaseMode = null;
        InstanceLeaseReleaseMode? secondReleaseMode = null;
        var firstLease = firstInstance.CreateLease((lease, mode) =>
        {
            firstReleaseMode = mode;
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
        var secondLease = secondInstance.CreateLease((lease, mode) =>
        {
            secondReleaseMode = mode;
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
        var handle = new LeasedInstanceHandle(
            firstLease,
            new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance),
            new PoolDemuxBindingTable(),
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 1);

        firstLease.NotifyFaulted(new InvalidOperationException("process crashed"));
        firstLease.NotifyReplaced(secondLease);
        await handle.EnsureConnectedAsync(CancellationToken.None);

        var disposeCoreAsync = typeof(LeasedInstanceHandle).GetMethod(
            "DisposeCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        disposeCoreAsync.ShouldNotBeNull();
        var disposeTask = (ValueTask)disposeCoreAsync.Invoke(handle, [InstanceLeaseReleaseMode.Immediate])!;
        await disposeTask;

        firstReleaseMode.ShouldBeNull();
        secondReleaseMode.ShouldBe(InstanceLeaseReleaseMode.Immediate);
    }

    [Fact]
    public async Task subscribe_keeps_binding_when_enumeration_is_canceled_and_dispose_removes_it()
    {
        var handler = new RecordingHttpMessageHandler();
        var instance = CreateInstance(handler);
        var lease = instance.CreateLease(static (releasedLease, _) =>
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
            lease,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 3);
        await handle.BindSessionAsync("oc-session-1", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var enumerator = handle.SubscribeEvents("oc-session-1", cts.Token).GetAsyncEnumerator(cts.Token);

        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        await instance.WaitForEventSubscriptionAsync("oc-session-1").WaitAsync(TimeSpan.FromSeconds(5));
        bindingTable.TryGetBinding(instance, "/repo/one", "oc-session-1", 3, out _).ShouldBeTrue();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => moveNextTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await enumerator.DisposeAsync();

        bindingTable.TryGetBinding(instance, "/repo/one", "oc-session-1", 3, out _).ShouldBeTrue();

        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);
        var secondEnumerator = handle.SubscribeEvents("oc-session-1", CancellationToken.None).GetAsyncEnumerator();
        var secondMove = secondEnumerator.MoveNextAsync().AsTask();
        var routedEvent = CreateSseEvent("message.updated", "oc-session-1");

        await stream.WriteAsync(routedEvent);

        (await secondMove.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        secondEnumerator.Current.ShouldBeSameAs(routedEvent);
        await secondEnumerator.DisposeAsync();

        await handle.DisposeAsync();

        bindingTable.TryGetBinding(instance, "/repo/one", "oc-session-1", 3, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task subscribe_yields_routed_events_and_fault_completion_keeps_binding_until_handle_dispose()
    {
        var handler = new RecordingHttpMessageHandler();
        var instance = CreateInstance(handler);
        var lease = instance.CreateLease(static (releasedLease, _) =>
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
            lease,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 3);

        await handle.BindSessionAsync("oc-session-1", CancellationToken.None);
        var enumerator = handle.SubscribeEvents("oc-session-1", CancellationToken.None).GetAsyncEnumerator();
        var firstMove = enumerator.MoveNextAsync().AsTask();
        await instance.WaitForEventSubscriptionAsync("oc-session-1").WaitAsync(TimeSpan.FromSeconds(5));
        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);
        var routedEvent = CreateSseEvent("message.updated", "oc-session-1");

        await stream.WriteAsync(routedEvent);

        (await firstMove.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        enumerator.Current.ShouldBeSameAs(routedEvent);

        lease.NotifyFaulted(new InvalidOperationException("process crashed"));
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse();
        await enumerator.DisposeAsync();

        bindingTable.TryGetBinding(instance, "/repo/one", "oc-session-1", 3, out _).ShouldBeTrue();
        await handle.DisposeAsync();
        bindingTable.TryGetBinding(instance, "/repo/one", "oc-session-1", 3, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task wait_for_event_subscription_delegates_to_current_instance()
    {
        var handler = new RecordingHttpMessageHandler();
        var instance = CreateInstance(handler);
        var lease = instance.CreateLease(static (releasedLease, _) =>
        {
            releasedLease.Instance.RemoveLease(releasedLease);
            return ValueTask.CompletedTask;
        });
        var handle = new LeasedInstanceHandle(
            lease,
            new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance),
            new PoolDemuxBindingTable(),
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 3);
        var waitTask = handle.WaitForEventSubscriptionAsync("oc-session-1", CancellationToken.None);

        instance.NotifyEventSubscriptionReady("oc-session-1");

        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();
    }

    [Fact]
    public void constructor_rejects_negative_generation_and_missing_http_client()
    {
        var demultiplexer = new SseEventDemultiplexer(new PoolDemuxBindingTable(), NullLogger<SseEventDemultiplexer>.Instance);
        var bindingTable = new PoolDemuxBindingTable();
        var leaseWithoutHttpClient = CreateLeaseWithoutHttpClient();

        Should.Throw<ArgumentOutOfRangeException>(() => new LeasedInstanceHandle(
            leaseWithoutHttpClient,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: -1));

        Should.Throw<InvalidOperationException>(() => new LeasedInstanceHandle(
            leaseWithoutHttpClient,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 1));
    }

    private static LeasedInstanceHandle CreateLeasedHandle(
        RecordingHttpMessageHandler handler,
        out PoolDemuxBindingTable bindingTable,
        out PooledOpenCodeInstance instance)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        instance = CreateInstance(openCodeHttpClient);
        var lease = instance.CreateLease(static (lease, _) =>
        {
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
        bindingTable = new PoolDemuxBindingTable();
        var demultiplexer = new SseEventDemultiplexer(
            bindingTable,
            NullLogger<SseEventDemultiplexer>.Instance);

        return new LeasedInstanceHandle(
            lease,
            demultiplexer,
            bindingTable,
            "/repo/one",
            "fleet-session-1",
            "user-1",
            Guid.NewGuid(),
            leaseGeneration: 7);
    }

    private static PooledOpenCodeInstance CreateInstance(RecordingHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        var openCodeHttpClient = new OpenCodeHttpClient(httpClient, NullLogger<OpenCodeHttpClient>.Instance);
        return CreateInstance(openCodeHttpClient);
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

    private static InstanceLease CreateLeaseWithoutHttpClient()
    {
        var instance = new PooledOpenCodeInstance(
            "key",
            $"instance-{Guid.NewGuid():N}",
            processId: 123,
            shutdownAsync: () => ValueTask.CompletedTask);
        return instance.CreateLease(static (lease, _) =>
        {
            lease.Instance.RemoveLease(lease);
            return ValueTask.CompletedTask;
        });
    }

    private static OpenCodeCommandRequest CreateCommandRequest()
    {
        return new OpenCodeCommandRequest
        {
            Command = "test",
            Arguments = string.Empty,
        };
    }

    private static OpenCodeSseEvent CreateSseEvent(string type, string sessionId)
    {
        var properties = System.Text.Json.JsonSerializer.SerializeToElement(new { sessionID = sessionId });
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

            await using var cancellation = ct.Register(stream.Cancel);
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

        public List<string> RequestBodies { get; } = [];

        public string ResponseContent { get; set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/event")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new HangingSseContent(cancellationToken),
                };
            }

            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class HangingSseContent(CancellationToken ct) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            new HangingSseStream(ct);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new HangingSseStream(ct));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new HangingSseStream(ct));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class HangingSseStream(CancellationToken ct) : Stream
    {
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
            try
            {
                Task.Delay(Timeout.InfiniteTimeSpan, ct).Wait(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }

            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
