using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

public sealed class PiHarnessSessionTests
{
    [Fact]
    public async Task send_prompt_events_idle_abort_and_stop_transition_through_expected_states()
    {
        await using var harness = CreateHarness();
        await using var session = harness.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        session.ProcessId.ShouldBe(4242);

        var promptTask = session.SendPromptAsync("hello pi", null, cts.Token);
        var prompt = await harness.ReadCommandAsync(cts.Token);
        prompt.GetProperty("type").GetString().ShouldBe("prompt");
        prompt.GetProperty("message").GetString().ShouldBe("hello pi");
        var promptId = prompt.GetProperty("id").GetString();
        promptId.ShouldNotBeNullOrWhiteSpace();
        session.Status.ShouldBe(HarnessSessionStatus.Idle);

        await harness.WriteResponseAsync(null, "prompt", true, null, cts.Token);
        await promptTask;

        session.Status.ShouldBe(HarnessSessionStatus.Running);

        var eventTask = ReadEventsAsync(session, 4, cts.Token);
        await harness.WriteEventAsync(new PiAgentStartEvent(), cts.Token);
        await harness.WriteEventAsync(new PiMessageStartEvent
        {
            Message = AssistantMessage(1000, new PiTextContent { Text = string.Empty }),
        }, cts.Token);
        await harness.WriteEventAsync(new PiMessageUpdateEvent
        {
            AssistantMessageEvent = new PiTextDeltaEvent { ContentIndex = 0, Delta = "ok" },
        }, cts.Token);
        await harness.WriteEventAsync(new PiIdleEvent(), cts.Token);

        var events = await eventTask;
        events.Select(evt => evt.Type).ShouldBe(new[]
        {
            EventTypes.SessionStatus,
            EventTypes.MessageCreated,
            EventTypes.MessagePartDelta,
            EventTypes.SessionStatus,
        });
        StatusType(events[0]).ShouldBe("busy");
        MessageId(events[1]).ShouldBe("pi-fleet-session-assistant-1000");
        Payload(events[2]).GetProperty("delta").GetString().ShouldBe("ok");
        StatusType(events[3]).ShouldBe("idle");
        session.Status.ShouldBe(HarnessSessionStatus.Idle);

        await session.AbortAsync(cts.Token);

        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        var abort = await harness.ReadCommandAsync(cts.Token);
        abort.GetProperty("type").GetString().ShouldBe("abort");

        await session.StopAsync(cts.Token);

        session.Status.ShouldBe(HarnessSessionStatus.Stopped);
        harness.Process.StopCount.ShouldBe(1);
        harness.Process.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task send_prompt_without_response_id_completes_and_does_not_emit_protocol_error()
    {
        await using var harness = CreateHarness();
        await using var session = harness.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var promptTask = session.SendPromptAsync("hello pi", null, cts.Token);
        var command = await harness.ReadCommandAsync(cts.Token);
        command.GetProperty("type").GetString().ShouldBe("prompt");
        command.GetProperty("id").GetString().ShouldNotBeNullOrWhiteSpace();
        session.Status.ShouldBe(HarnessSessionStatus.Idle);

        await harness.WriteResponseAsync(null, "prompt", true, null, cts.Token);
        await promptTask;

        session.Status.ShouldBe(HarnessSessionStatus.Running);
        var evt = await TryReadHarnessEventAsync(session, TimeSpan.FromMilliseconds(100));
        evt.ShouldBeNull();
    }

    [Fact]
    public async Task send_prompt_failed_response_without_id_throws_pi_error_and_leaves_status_idle()
    {
        await using var harness = CreateHarness();
        await using var session = harness.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var promptTask = session.SendPromptAsync("hello pi", null, cts.Token);
        var command = await harness.ReadCommandAsync(cts.Token);
        command.GetProperty("type").GetString().ShouldBe("prompt");
        command.GetProperty("id").GetString().ShouldNotBeNullOrWhiteSpace();

        await harness.WriteResponseAsync(null, "prompt", false, null, "prompt rejected", cts.Token);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => promptTask);
        exception.Message.ShouldBe("prompt rejected");
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public async Task missing_id_response_with_multiple_matching_pending_requests_emits_protocol_error()
    {
        await using var harness = CreateHarness();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstRequest = harness.SendRequestAsync(
            new PiPromptCommand { Id = "prompt-1", Message = "one" },
            cts.Token);
        var secondRequest = harness.SendRequestAsync(
            new PiPromptCommand { Id = "prompt-2", Message = "two" },
            cts.Token);

        var firstCommand = await harness.ReadCommandAsync(cts.Token);
        firstCommand.GetProperty("id").GetString().ShouldBe("prompt-1");
        var secondCommand = await harness.ReadCommandAsync(cts.Token);
        secondCommand.GetProperty("id").GetString().ShouldBe("prompt-2");

        await harness.WriteResponseAsync(null, "prompt", true, null, cts.Token);

        var evt = await harness.ReadClientEventAsync(cts.Token);
        var protocolError = evt.ShouldBeOfType<PiProtocolErrorEvent>();
        protocolError.Kind.ShouldBe("response_missing_id");
        protocolError.CommandType.ShouldBe("prompt");

        await harness.WriteResponseAsync("prompt-1", "prompt", true, null, cts.Token);
        await harness.WriteResponseAsync("prompt-2", "prompt", true, null, cts.Token);
        (await firstRequest).Success.ShouldBeTrue();
        (await secondRequest).Success.ShouldBeTrue();
    }

    [Fact]
    public async Task get_messages_maps_messages_before_limit_and_has_more()
    {
        await using var harness = CreateHarness();
        await using var session = harness.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var messagesTask = session.GetMessagesAsync(new MessageQuery(1, "assistant-3"), cts.Token);
        var command = await harness.ReadCommandAsync(cts.Token);
        command.GetProperty("type").GetString().ShouldBe("get_messages");
        var requestId = command.GetProperty("id").GetString();
        requestId.ShouldNotBeNullOrWhiteSpace();

        await harness.WriteResponseAsync(requestId, "get_messages", true, new
        {
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[] { new { type = "text", text = "question" } },
                    timestamp = 1000,
                    responseId = "user-1",
                },
                new
                {
                    role = "assistant",
                    content = new object[] { new { type = "text", text = "answer" } },
                    timestamp = 2000,
                    responseId = "assistant-2",
                    responseModel = "model-a",
                },
                new
                {
                    role = "assistant",
                    content = new object[] { new { type = "text", text = "later" } },
                    timestamp = 3000,
                    responseId = "assistant-3",
                },
            },
            hasMore = false,
        }, cts.Token);

        var page = await messagesTask;

        page.HasMore.ShouldBeTrue();
        page.Messages.Count.ShouldBe(1);
        page.Messages[0].Id.ShouldBe("user-1");
        page.Messages[0].Role.ShouldBe("user");
        page.Messages[0].TextContent.ShouldBe("question");
    }

    [Fact]
    public async Task check_health_get_state_success_and_failure_updates_status()
    {
        await using var harness = CreateHarness();
        await using var session = harness.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var healthyTask = session.CheckHealthAsync(cts.Token);
        var command = await harness.ReadCommandAsync(cts.Token);
        command.GetProperty("type").GetString().ShouldBe("get_state");
        var requestId = command.GetProperty("id").GetString();
        requestId.ShouldNotBeNullOrWhiteSpace();

        await harness.WriteResponseAsync(requestId, "get_state", true, new
        {
            isStreaming = true,
            isCompacting = false,
            sessionFile = "/tmp/pi-session.json",
            sessionId = "pi-session-id",
            autoCompactionEnabled = true,
            messageCount = 3,
            pendingMessageCount = 0,
        }, cts.Token);

        var healthy = await healthyTask;
        healthy.Healthy.ShouldBeTrue();
        healthy.Message.ShouldBeNull();
        session.Status.ShouldBe(HarnessSessionStatus.Running);
        session.ResumeToken.ShouldNotBeNull();

        var unhealthyTask = session.CheckHealthAsync(cts.Token);
        command = await harness.ReadCommandAsync(cts.Token);
        requestId = command.GetProperty("id").GetString();
        requestId.ShouldNotBeNullOrWhiteSpace();
        await harness.WriteResponseAsync(requestId, "get_state", false, null, "state failed", cts.Token);

        var unhealthy = await unhealthyTask;
        unhealthy.Healthy.ShouldBeFalse();
        unhealthy.Message.ShouldBe("state failed");

        harness.Process.IsRunning = false;
        var stopped = await session.CheckHealthAsync(cts.Token);

        stopped.Healthy.ShouldBeFalse();
        stopped.Message.ShouldBe("Pi process is not running.");
    }

    [Fact]
    public async Task dispose_completes_subscription_stream_and_stops_process_once()
    {
        await using var harness = CreateHarness();
        var session = harness.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var enumerator = session.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var completedTask = enumerator.MoveNextAsync().AsTask();

        await session.DisposeAsync();

        var completed = await completedTask;
        completed.ShouldBeFalse();
        session.Status.ShouldBe(HarnessSessionStatus.Stopped);
        harness.Process.StopCount.ShouldBe(1);
        harness.Process.DisposeCount.ShouldBe(1);
        harness.Process.IsRunning.ShouldBeFalse();

        await session.DisposeAsync();
        harness.Process.StopCount.ShouldBe(1);
        harness.Process.DisposeCount.ShouldBe(1);
    }

    private static PiSessionHarness CreateHarness()
        => new();

    private static PiMessage AssistantMessage(long timestamp, params PiContentBlock[] content)
        => new()
        {
            Role = "assistant",
            Content = content,
            Timestamp = timestamp,
        };

    private static async Task<IReadOnlyList<HarnessEvent>> ReadEventsAsync(
        PiHarnessSession session,
        int count,
        CancellationToken ct)
    {
        var events = new List<HarnessEvent>(count);
        await foreach (var evt in session.SubscribeAsync(ct).WithCancellation(ct))
        {
            events.Add(evt);
            if (events.Count == count)
                break;
        }

        return events;
    }

    private static async Task<HarnessEvent?> TryReadHarnessEventAsync(PiHarnessSession session, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await using var enumerator = session.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            return await enumerator.MoveNextAsync() ? enumerator.Current : null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null;
        }
    }

    private static JsonElement Payload(HarnessEvent evt)
    {
        evt.Payload.HasValue.ShouldBeTrue();
        return evt.Payload.Value;
    }

    private static JsonElement Status(HarnessEvent evt)
        => Payload(evt).GetProperty("status");

    private static string? StatusType(HarnessEvent evt)
        => Status(evt).GetProperty("type").GetString();

    private static string? MessageId(HarnessEvent evt)
        => Payload(evt).GetProperty("info").GetProperty("id").GetString();

    private sealed class PiSessionHarness : IAsyncDisposable
    {
        private readonly MemoryStream _clientInput = new();
        private readonly TestInputStream _clientOutput = new();
        private readonly PiJsonlClient _client;
        private readonly StreamWriter _stdinWriter;
        private readonly StreamReader _stdoutReader;

        public PiSessionHarness()
        {
            _stdinWriter = new StreamWriter(_clientInput, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            _stdoutReader = new StreamReader(_clientOutput, Encoding.UTF8, leaveOpen: true);
            _client = new PiJsonlClient(_stdinWriter, _stdoutReader, NullLogger<PiJsonlClient>.Instance);
        }

        public TestPiProcessManager Process { get; } = new();

        public PiHarnessSession CreateSession()
            => new(
                "pi-instance",
                "fleet-session",
                Process,
                _client,
                TimeSpan.FromMilliseconds(50),
                NullLogger<PiHarnessSession>.Instance);

        public Task<PiResponseEvent> SendRequestAsync(PiCommand command, CancellationToken ct)
            => _client.SendRequestAsync(command, ct);

        public async Task<PiEvent> ReadClientEventAsync(CancellationToken ct)
            => await _client.Events.ReadAsync(ct).ConfigureAwait(false);

        public async Task<JsonElement> ReadCommandAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (TryReadLine(out var line))
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }

                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }

        public async Task WriteEventAsync(PiEvent evt, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(evt, PiJsonContext.Default.PiEvent);
            await _clientOutput.WriteLineAsync(json, ct).ConfigureAwait(false);
        }

        public async Task WriteResponseAsync(
            string? id,
            string command,
            bool success,
            object? data,
            CancellationToken ct)
        {
            await WriteResponseAsync(id, command, success, data, error: null, ct).ConfigureAwait(false);
        }

        public async Task WriteResponseAsync(
            string? id,
            string command,
            bool success,
            object? data,
            string? error,
            CancellationToken ct)
        {
            var response = new PiResponseEvent
            {
                Id = id,
                Command = command,
                Success = success,
                Data = data is null ? null : JsonSerializer.SerializeToElement(data),
                Error = error,
            };

            await WriteEventAsync(response, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _stdinWriter.Dispose();
            _stdoutReader.Dispose();
            await _clientInput.DisposeAsync().ConfigureAwait(false);
            await _clientOutput.DisposeAsync().ConfigureAwait(false);
        }

        private bool TryReadLine(out string line)
        {
            _stdinWriter.Flush();
            var buffer = _clientInput.GetBuffer();
            var length = (int)_clientInput.Length;

            for (var index = 0; index < length; index++)
            {
                if (buffer[index] != (byte)'\n')
                    continue;

                line = Encoding.UTF8.GetString(buffer, 0, index).TrimEnd('\r');
                var remaining = length - index - 1;
                if (remaining > 0)
                    Buffer.BlockCopy(buffer, index + 1, buffer, 0, remaining);

                _clientInput.SetLength(remaining);
                _clientInput.Position = remaining;
                return true;
            }

            line = string.Empty;
            return false;
        }
    }

    private sealed class TestPiProcessManager : IPiProcessManager
    {
        public event EventHandler<int>? ProcessExited
        {
            add { }
            remove { }
        }

        public bool IsRunning { get; set; } = true;

        public int? ProcessId => 4242;

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StopAsync(TimeSpan timeout)
        {
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestInputStream : Stream
    {
        private readonly Queue<byte> _buffer = new();
        private readonly SemaphoreSlim _signal = new(0);
        private bool _completed;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (_buffer)
            {
                foreach (var value in bytes)
                    _buffer.Enqueue(value);
            }

            _signal.Release(bytes.Length);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryDequeue(buffer, out var count))
                    return count;

                if (_completed)
                    return 0;

                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Complete();
            return base.DisposeAsync();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        private bool TryDequeue(Memory<byte> destination, out int count)
        {
            lock (_buffer)
            {
                if (_buffer.Count == 0)
                {
                    count = 0;
                    return false;
                }

                count = Math.Min(destination.Length, _buffer.Count);
                for (var index = 0; index < count; index++)
                    destination.Span[index] = _buffer.Dequeue();

                return true;
            }
        }

        private void Complete()
        {
            if (_disposed)
                return;

            _disposed = true;
            _completed = true;
            _signal.Release();
            _signal.Dispose();
        }
    }
}
