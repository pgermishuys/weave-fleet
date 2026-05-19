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
    private readonly ConcurrentQueue<ScriptedLlmResponse> _responses = new();

    /// <summary>Enqueue a response to be returned on the next request.</summary>
    public void Enqueue(ScriptedLlmResponse response) => _responses.Enqueue(response);

    /// <summary>Try to dequeue the next scripted response.</summary>
    public bool TryDequeue(out ScriptedLlmResponse? response) => _responses.TryDequeue(out response);

    /// <summary>Number of responses currently queued.</summary>
    public int Count => _responses.Count;
}
