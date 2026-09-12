using System.Collections.Concurrent;

namespace FakeLlmServer;

/// <summary>
/// A scripted LLM response to return from the fake server.
/// </summary>
public sealed record ScriptedLlmResponse
{
    /// <summary>Text content to return as the assistant message.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional tool calls to include in the response.</summary>
    public IReadOnlyList<ScriptedToolCall>? ToolCalls { get; init; }

    /// <summary>Input token count to report in usage.</summary>
    public int InputTokens { get; init; } = 10;

    /// <summary>Output token count to report in usage.</summary>
    public int OutputTokens { get; init; } = 10;

    /// <summary>Stop reason to report (default: "stop").</summary>
    public string StopReason { get; init; } = "stop";
}

/// <summary>A tool call to include in a scripted response.</summary>
public sealed record ScriptedToolCall(string Id, string Name, string InputJson);

/// <summary>
/// Thread-safe store of scripted LLM responses.
/// The fake server dequeues one response per incoming request.
/// </summary>
public sealed class ScriptedResponseStore
{
    private readonly ConcurrentQueue<Func<string, ScriptedLlmResponse>> _responses = new();
    private readonly ConcurrentQueue<string> _requests = new();

    /// <summary>Enqueue a response to be returned on the next request.</summary>
    public void Enqueue(ScriptedLlmResponse response) => _responses.Enqueue(_ => response);

    /// <summary>
    /// Enqueue a response built from the request body it answers, e.g. to call a tool with an id that an
    /// earlier tool result returned.
    /// </summary>
    public void Enqueue(Func<string, ScriptedLlmResponse> respond) => _responses.Enqueue(respond);

    /// <summary>
    /// When set, a request that offers no tools gets this response without using up the queue. Harnesses
    /// make such side requests at their own timing, e.g. OpenCode's session title generation.
    /// </summary>
    public ScriptedLlmResponse? ToolLessResponse { get; set; }

    /// <summary>Every request body received, in order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Try to dequeue the next scripted response.</summary>
    public bool TryDequeue(out ScriptedLlmResponse? response) => TryDequeue(string.Empty, out response);

    /// <summary>Try to dequeue the next scripted response for <paramref name="requestBody"/>.</summary>
    public bool TryDequeue(string requestBody, out ScriptedLlmResponse? response)
    {
        if (_responses.TryDequeue(out var respond))
        {
            response = respond(requestBody);
            return true;
        }

        response = null;
        return false;
    }

    internal void Record(string requestBody) => _requests.Enqueue(requestBody);

    /// <summary>Number of responses currently queued.</summary>
    public int Count => _responses.Count;
}
