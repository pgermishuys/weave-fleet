using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>
/// The JSON-RPC peer, driven over in-memory pipes by a test that plays the host: it writes raw bytes and reads the lines
/// Fleet answers with.
/// </summary>
public sealed class ModHostRpcTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly Pipe _toFleet = new();
    private readonly Pipe _fromFleet = new();
    private readonly FakeCalls _calls = new();
    private readonly CapturingLogger _log = new();
    private readonly StreamReader _fleetOutput;
    private ModHostRpc? _rpc;

    public ModHostRpcTests() => _fleetOutput = new StreamReader(_fromFleet.Reader.AsStream(), new UTF8Encoding(false));

    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null)
            await _rpc.DisposeAsync();
    }

    private ModHostRpc Rpc(int maxLineBytes = 8 * 1024 * 1024)
        => _rpc = new ModHostRpc(_toFleet.Reader.AsStream(), _fromFleet.Writer.AsStream(), _calls, _log, maxLineBytes);

    private async Task SendAsync(string text) => await SendBytesAsync(Encoding.UTF8.GetBytes(text));

    private async Task SendBytesAsync(byte[] bytes)
    {
        var stream = _toFleet.Writer.AsStream();
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private async Task<JsonElement> ReadAsync()
    {
        var line = await _fleetOutput.ReadLineAsync().WaitAsync(Wait);
        line.ShouldNotBeNull();
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task A_request_is_answered_with_the_result_and_ids_start_at_one()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("check", Json("""{"root":"/x"}"""), Wait, CancellationToken.None);

        var sent = await ReadAsync();
        sent.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        sent.GetProperty("id").GetInt32().ShouldBe(1);
        sent.GetProperty("method").GetString().ShouldBe("check");
        sent.GetProperty("params").GetProperty("root").GetString().ShouldBe("/x");

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""" + "\n");
        (await pending).GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_message_written_one_byte_at_a_time_is_read_whole()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();

        foreach (var b in Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":"slow"}""" + "\n"))
            await SendBytesAsync([b]);

        (await pending).GetString().ShouldBe("slow");
    }

    [Fact]
    public async Task Several_messages_in_one_write_are_all_read()
    {
        var rpc = Rpc();
        var first = rpc.RequestAsync("a", null, Wait, CancellationToken.None);
        var second = rpc.RequestAsync("b", null, Wait, CancellationToken.None);
        await ReadAsync();
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":1}""" + "\n" + """{"jsonrpc":"2.0","id":2,"result":2}""" + "\n");

        (await first).GetInt32().ShouldBe(1);
        (await second).GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_line_ending_in_carriage_return_and_newline_is_read()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"crlf"}""" + "\r\n");

        (await pending).GetString().ShouldBe("crlf");
    }

    [Fact]
    public async Task A_multi_byte_character_split_across_writes_is_decoded()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();

        var bytes = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":"caf""" + "é€" + "\"}\n");
        var cut = Array.IndexOf(bytes, (byte)0xE2) + 1; // inside the three-byte euro sign
        await SendBytesAsync(bytes[..cut]);
        await SendBytesAsync(bytes[cut..]);

        (await pending).GetString().ShouldBe("café€");
    }

    [Fact]
    public async Task A_line_over_the_limit_is_dropped_and_the_next_line_still_works()
    {
        var rpc = Rpc(maxLineBytes: 256);
        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();

        await SendAsync(new string('x', 100));
        await SendAsync(new string('y', 300));
        await SendAsync(new string('z', 100) + "\n");
        var refusal = await ReadAsync();
        refusal.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        refusal.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"after"}""" + "\n");
        (await pending).GetString().ShouldBe("after");
        _log.Messages.ShouldContain(m => m.Contains("256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_that_is_not_json_is_answered_with_a_parse_error_and_the_loop_goes_on()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();

        await SendAsync("{not json\n");
        var error = await ReadAsync();
        error.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        error.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":true}""" + "\n");
        (await pending).GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_request_without_a_string_method_is_answered_with_invalid_request()
    {
        Rpc();
        await SendAsync("""{"jsonrpc":"2.0","id":7,"method":5}""" + "\n");

        var error = await ReadAsync();
        error.GetProperty("id").GetInt32().ShouldBe(7);
        error.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);
    }

    [Fact]
    public async Task Answers_arriving_out_of_order_reach_their_own_requests()
    {
        var rpc = Rpc();
        var first = rpc.RequestAsync("a", null, Wait, CancellationToken.None);
        var second = rpc.RequestAsync("b", null, Wait, CancellationToken.None);
        await ReadAsync();
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":2,"result":"second"}""" + "\n");
        (await second).GetString().ShouldBe("second");
        first.IsCompleted.ShouldBeFalse();
        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"first"}""" + "\n");
        (await first).GetString().ShouldBe("first");
    }

    [Fact]
    public async Task A_request_not_answered_in_time_times_out_and_a_late_answer_is_ignored()
    {
        var rpc = Rpc();
        var slow = rpc.RequestAsync("dispatch", null, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await ReadAsync();
        await Should.ThrowAsync<TimeoutException>(slow);

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"late"}""" + "\n");
        var next = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        var sent = await ReadAsync();
        sent.GetProperty("id").GetInt32().ShouldBe(2);
        await SendAsync("""{"jsonrpc":"2.0","id":2,"result":"fresh"}""" + "\n");
        (await next).GetString().ShouldBe("fresh");
        _log.Messages.ShouldContain(m => m.Contains("no waiting request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_request_throws_and_its_late_answer_is_ignored()
    {
        var rpc = Rpc();
        using var cts = new CancellationTokenSource();
        var pending = rpc.RequestAsync("check", null, Wait, cts.Token);
        await ReadAsync();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(pending);

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"late"}""" + "\n");
        var next = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();
        await SendAsync("""{"jsonrpc":"2.0","id":2,"result":"ok"}""" + "\n");
        (await next).GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task An_error_answer_throws_with_its_code_message_and_data()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("load", null, Wait, CancellationToken.None);
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"test-chips doesn't load","data":{"ok":false,"errors":[{"code":"x"}]}}}""" + "\n");

        var thrown = await Should.ThrowAsync<ModHostRpcException>(pending);
        thrown.Code.ShouldBe(ModHostErrorCodes.NotLoaded);
        thrown.Message.ShouldBe("test-chips doesn't load");
        thrown.Data.ShouldNotBeNull().GetProperty("errors")[0].GetProperty("code").GetString().ShouldBe("x");
    }

    [Fact]
    public async Task A_request_from_the_host_is_answered_while_a_request_to_it_is_still_pending()
    {
        _calls.OnRequest = (method, p) => Task.FromResult(Json($$"""{"echo":"{{method}}","key":{{p.GetProperty("key").GetRawText()}}}"""));
        var rpc = Rpc();
        var dispatch = rpc.RequestAsync("dispatch", null, Wait, CancellationToken.None);
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":"h1","method":"store.get","params":{"key":"k"}}""" + "\n");
        var answer = await ReadAsync();
        answer.GetProperty("id").GetString().ShouldBe("h1");
        answer.GetProperty("result").GetProperty("echo").GetString().ShouldBe("store.get");
        dispatch.IsCompleted.ShouldBeFalse();

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":{"done":true}}""" + "\n");
        (await dispatch).GetProperty("done").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_slow_handler_does_not_stop_the_loop_reading_other_answers()
    {
        var release = new TaskCompletionSource<JsonElement>();
        _calls.OnRequest = (_, _) => release.Task;
        var rpc = Rpc();
        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":"h1","method":"store.get"}""" + "\n");
        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"through"}""" + "\n");
        (await pending).GetString().ShouldBe("through");

        release.SetResult(Json("null"));
        (await ReadAsync()).GetProperty("id").GetString().ShouldBe("h1");
    }

    [Fact]
    public async Task A_handler_throwing_an_rpc_exception_answers_with_that_error()
    {
        _calls.OnRequest = (_, _) => throw new ModHostRpcException(ModHostErrorCodes.InvalidParams, "key must be a string", Json("""{"field":"key"}"""));
        Rpc();
        await SendAsync("""{"jsonrpc":"2.0","id":3,"method":"store.get","params":{}}""" + "\n");

        var error = (await ReadAsync()).GetProperty("error");
        error.GetProperty("code").GetInt32().ShouldBe(-32602);
        error.GetProperty("message").GetString().ShouldBe("key must be a string");
        error.GetProperty("data").GetProperty("field").GetString().ShouldBe("key");
    }

    [Fact]
    public async Task A_handler_throwing_anything_else_answers_with_an_internal_error()
    {
        _calls.OnRequest = (_, _) => throw new InvalidOperationException("the store broke");
        Rpc();
        await SendAsync("""{"jsonrpc":"2.0","id":3,"method":"store.get"}""" + "\n");

        var error = (await ReadAsync()).GetProperty("error");
        error.GetProperty("code").GetInt32().ShouldBe(-32603);
        error.GetProperty("message").GetString().ShouldBe("the store broke");
    }

    [Fact]
    public async Task A_result_over_the_limit_is_answered_with_a_result_too_large_error()
    {
        _calls.OnRequest = (_, _) => Task.FromResult(Json("\"" + new string('r', 400) + "\""));
        Rpc(maxLineBytes: 256);
        await SendAsync("""{"jsonrpc":"2.0","id":3,"method":"store.get"}""" + "\n");

        var answer = await ReadAsync();
        answer.GetProperty("id").GetInt32().ShouldBe(3);
        answer.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32603);
        answer.GetProperty("error").GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("too large");
    }

    [Fact]
    public async Task A_notification_is_delivered_to_the_calls()
    {
        Rpc();
        await SendAsync("""{"jsonrpc":"2.0","method":"invalidate","params":{"modId":"demo-mod@v1"}}""" + "\n");

        var (method, parameters) = await _calls.Notifications.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        method.ShouldBe("invalidate");
        parameters.GetProperty("modId").GetString().ShouldBe("demo-mod@v1");
    }

    [Fact]
    public async Task A_notification_handler_that_throws_does_not_stop_the_loop()
    {
        _calls.ThrowOnNotification = true;
        var rpc = Rpc();
        await SendAsync("""{"jsonrpc":"2.0","method":"log","params":{}}""" + "\n");

        var pending = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        await ReadAsync();
        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":"alive"}""" + "\n");
        (await pending).GetString().ShouldBe("alive");
    }

    [Fact]
    public async Task When_input_ends_pending_requests_fail_later_ones_fail_at_once_and_Closed_completes()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("dispatch", null, Wait, CancellationToken.None);
        await ReadAsync();

        await _toFleet.Writer.CompleteAsync();

        await Should.ThrowAsync<ModHostClosedException>(pending);
        await rpc.Closed.WaitAsync(Wait);
        await Should.ThrowAsync<ModHostClosedException>(rpc.RequestAsync("check", null, Wait, CancellationToken.None));
    }

    [Fact]
    public async Task A_request_over_the_limit_is_refused_without_being_sent()
    {
        var rpc = Rpc(maxLineBytes: 256);

        var thrown = await Should.ThrowAsync<ModHostRpcException>(
            rpc.RequestAsync("load", Json("\"" + new string('p', 400) + "\""), Wait, CancellationToken.None));

        thrown.Code.ShouldBe(ModHostErrorCodes.Internal);
        thrown.Message.ShouldContain("too large");

        var next = rpc.RequestAsync("check", null, Wait, CancellationToken.None);
        (await ReadAsync()).GetProperty("method").GetString().ShouldBe("check");
        await SendAsync("""{"jsonrpc":"2.0","id":2,"result":null}""" + "\n");
        await next;
    }

    private sealed class FakeCalls : IModHostCalls
    {
        public Func<string, JsonElement, Task<JsonElement>> OnRequest { get; set; } = (_, _) => Task.FromResult(default(JsonElement));

        public bool ThrowOnNotification { get; set; }

        public Channel<(string Method, JsonElement Parameters)> Notifications { get; } = Channel.CreateUnbounded<(string, JsonElement)>();

        public Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct) => OnRequest(method, parameters);

        public void HandleNotification(string method, JsonElement parameters)
        {
            Notifications.Writer.TryWrite((method, parameters));
            if (ThrowOnNotification)
                throw new InvalidOperationException("notification handler broke");
        }
    }
}

/// <summary>An <see cref="ILogger"/> that keeps the formatted messages.</summary>
internal sealed class CapturingLogger : ILogger<ModHostConnectionFactory>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _messages.Enqueue(formatter(state, exception));
}
