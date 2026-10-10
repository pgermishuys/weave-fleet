using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>The JSON-RPC peer over in-memory pipes, with the test playing the host.</summary>
public sealed class ModHostRpcTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly Pipe _toFleet = new();
    private readonly Pipe _fromFleet = new();
    private readonly StreamReader _fleetOutput;
    private ModHostRpc? _rpc;

    public ModHostRpcTests() => _fleetOutput = new StreamReader(_fromFleet.Reader.AsStream(), new UTF8Encoding(false));

    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null)
            await _rpc.DisposeAsync();
    }

    private ModHostRpc Rpc(int maxLineBytes = 8 * 1024 * 1024)
        => _rpc = new ModHostRpc(_toFleet.Reader.AsStream(), _fromFleet.Writer.AsStream(), new Calls(), NullLogger.Instance, maxLineBytes);

    private async Task SendAsync(string text)
    {
        var stream = _toFleet.Writer.AsStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        await stream.FlushAsync();
    }

    private async Task<JsonElement> ReadAsync()
    {
        var line = await _fleetOutput.ReadLineAsync().WaitAsync(Wait);
        return JsonDocument.Parse(line!).RootElement.Clone();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task A_request_gets_its_result_and_ids_start_at_one()
    {
        var pending = Rpc().RequestAsync("check", Json("""{"root":"/x"}"""), Wait);
        var sent = await ReadAsync();
        sent.GetProperty("id").GetInt32().ShouldBe(1);
        sent.GetProperty("method").GetString().ShouldBe("check");

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""" + "\n");
        (await pending).GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_message_split_into_one_byte_writes_is_read_whole()
    {
        var pending = Rpc().RequestAsync("a", null, Wait);
        await ReadAsync();
        foreach (var c in """{"jsonrpc":"2.0","id":1,"result":7}""" + "\n")
            await SendAsync(c.ToString());

        (await pending).GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task Two_messages_in_one_write_and_CRLF_both_work()
    {
        var rpc = Rpc();
        var first = rpc.RequestAsync("a", null, Wait);
        var second = rpc.RequestAsync("b", null, Wait);
        await ReadAsync();
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":1,"result":1}""" + "\r\n" + """{"jsonrpc":"2.0","id":2,"result":2}""" + "\n");
        (await first).GetInt32().ShouldBe(1);
        (await second).GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_line_over_the_limit_is_dropped_and_the_next_one_is_answered()
    {
        var pending = Rpc(maxLineBytes: 64).RequestAsync("a", null, Wait);
        await ReadAsync();

        await SendAsync(new string('x', 500) + "\n" + """{"jsonrpc":"2.0","id":1,"result":3}""" + "\n");
        (await pending).GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Bad_JSON_is_answered_with_a_parse_error()
    {
        Rpc();
        await SendAsync("{nope\n");

        var reply = await ReadAsync();
        reply.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(ModHostErrorCodes.ParseError);
    }

    [Fact]
    public async Task An_error_answer_throws_with_its_code_and_data()
    {
        var pending = Rpc().RequestAsync("a", null, Wait);
        await ReadAsync();
        await SendAsync("""{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"no","data":{"why":"x"}}}""" + "\n");

        var e = await Should.ThrowAsync<ModHostRpcException>(pending);
        e.Code.ShouldBe(-32001);
        e.Message.ShouldBe("no");
        e.Data!.Value.GetProperty("why").GetString().ShouldBe("x");
    }

    [Fact]
    public async Task An_incoming_request_is_answered_while_an_outgoing_one_waits()
    {
        var pending = Rpc().RequestAsync("a", null, Wait);
        await ReadAsync();

        await SendAsync("""{"jsonrpc":"2.0","id":"h1","method":"store.get","params":{}}""" + "\n");
        var reply = await ReadAsync();
        reply.GetProperty("id").GetString().ShouldBe("h1");
        reply.GetProperty("result").GetProperty("from").GetString().ShouldBe("fleet");
        pending.IsCompleted.ShouldBeFalse();

        await SendAsync("""{"jsonrpc":"2.0","id":"h2","method":"boom","params":{}}""" + "\n");
        (await ReadAsync()).GetProperty("error").GetProperty("code").GetInt32().ShouldBe(ModHostErrorCodes.Internal);
    }

    [Fact]
    public async Task Input_ending_fails_every_pending_request_as_closed()
    {
        var rpc = Rpc();
        var pending = rpc.RequestAsync("a", null, Wait);
        await ReadAsync();

        await _toFleet.Writer.CompleteAsync();
        await Should.ThrowAsync<ModHostClosedException>(pending);
        await Should.ThrowAsync<ModHostClosedException>(rpc.RequestAsync("b", null, Wait));
    }

    [Fact]
    public async Task A_peer_that_never_answers_times_out()
    {
        var pending = Rpc().RequestAsync("a", null, TimeSpan.FromMilliseconds(100));
        await ReadAsync();

        await Should.ThrowAsync<TimeoutException>(pending);
    }

    [Fact]
    public async Task A_peer_that_never_reads_times_out_the_write_and_the_requests_behind_it()
    {
        // A pipe nobody reads, which stops accepting after 1 KiB.
        var stuck = new Pipe(new PipeOptions(pauseWriterThreshold: 1024, resumeWriterThreshold: 512));
        var rpc = _rpc = new ModHostRpc(_toFleet.Reader.AsStream(), stuck.Writer.AsStream(), new Calls(), NullLogger.Instance);
        var big = Json($"\"{new string('x', 1024 * 1024)}\"");

        var first = rpc.RequestAsync("big", big, TimeSpan.FromMilliseconds(200));
        var behind = rpc.RequestAsync("small", null, TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<TimeoutException>(first).WaitAsync(Wait);
        await Should.ThrowAsync<TimeoutException>(behind).WaitAsync(Wait);
    }

    private sealed class Calls : IModHostCalls
    {
        public Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
            => method == "boom" ? throw new InvalidOperationException("boom") : Task.FromResult(Json("""{"from":"fleet"}"""));

        public void HandleNotification(string method, JsonElement parameters)
        {
        }
    }
}
