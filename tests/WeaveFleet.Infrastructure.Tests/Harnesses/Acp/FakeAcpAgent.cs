using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

/// <summary>An ACP agent in memory: the client talks to it over pipes, and the test scripts what it answers and says.</summary>
internal sealed class FakeAcpAgent : IAcpAgent
{
    private readonly Pipe _toAgent = new();
    private readonly Pipe _fromAgent = new();
    private readonly StreamWriter _out;
    private readonly Task _loop;

    public FakeAcpAgent()
    {
        _out = new StreamWriter(_fromAgent.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        var input = new StreamWriter(_toAgent.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        Client = new AcpClient(new StreamReader(_fromAgent.Reader.AsStream()), input, NullLogger.Instance);
        _loop = Task.Run(RunAsync);
    }

    public AcpClient Client { get; }
    public int ProcessId => 1;
    public bool IsRunning { get; private set; } = true;
    public string Stderr { get; set; } = "";

    /// <summary>Every message the client sent, in order.</summary>
    public List<(string Method, JsonElement Params)> Received { get; } = [];

    /// <summary>Answers a request with a result; it may send updates first. Throwing sends an error.</summary>
    public Func<string, JsonElement, Task<JsonNode?>> Handle { get; set; } = (_, _) => Task.FromResult<JsonNode?>(new JsonObject());

    public void Send(JsonNode message) => _out.WriteLine(message.ToJsonString());

    public void Update(string sessionId, JsonNode update)
        => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update } });

    /// <summary>The agent's own request to the client; the answer arrives as the returned task.</summary>
    public async Task<JsonElement> AskAsync(string method, JsonNode parameters)
    {
        var id = 9000 + Received.Count;
        var waiting = new TaskCompletionSource<JsonElement>();
        _pendingAsks[id] = waiting;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pendingAsks = [];

    public void Die()
    {
        IsRunning = false;
        _fromAgent.Writer.Complete();
    }

    private async Task RunAsync()
    {
        using var reader = new StreamReader(_toAgent.Reader.AsStream());
        while (await reader.ReadLineAsync() is { } line)
        {
            var message = JsonDocument.Parse(line).RootElement.Clone();
            var hasMethod = message.TryGetProperty("method", out var method);
            if (!hasMethod)
            {
                if (message.TryGetProperty("id", out var answered) && _pendingAsks.TryGetValue(answered.GetInt64(), out var ask))
                    ask.TrySetResult(message.GetProperty("result"));
                continue;
            }

            var parameters = message.TryGetProperty("params", out var p) ? p : default;
            lock (Received)
                Received.Add((method.GetString()!, parameters));
            if (!message.TryGetProperty("id", out var id))
                continue;

            _ = Task.Run(async () =>
            {
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.GetInt64() };
                try
                {
                    reply["result"] = await Handle(method.GetString()!, parameters);
                }
                catch (Exception ex)
                {
                    reply["error"] = new JsonObject { ["code"] = ex is AcpException acp ? acp.Code : -32603, ["message"] = ex.Message };
                }

                Send(reply);
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        await _toAgent.Writer.CompleteAsync();
        await _fromAgent.Writer.CompleteAsync();
        await Client.DisposeAsync();
    }
}
