using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FakeLlmServer;
using Microsoft.Extensions.AI;

namespace NuCode.ConformanceTests.Fakes;

/// <summary>
/// A fake <see cref="IChatClient"/> that returns scripted responses from a queue.
/// Used by <see cref="NuCode.NuCodeFixture"/> to control LLM responses in tests
/// without making real API calls.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly ConcurrentQueue<ScriptedLlmResponse> _responses = new();

    /// <summary>Enqueue a response to be returned on the next streaming call.</summary>
    public void Enqueue(ScriptedLlmResponse response) => _responses.Enqueue(response);

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_responses.TryDequeue(out var scripted) || scripted is null)
        {
            return Task.FromResult(new ChatResponse([]));
        }

        var contents = BuildContents(scripted);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_responses.TryDequeue(out var scripted) || scripted is null)
        {
            yield break;
        }

        await Task.Yield();

        if (scripted.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in scripted.ToolCalls)
            {
                var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.InputJson)
                    ?? new Dictionary<string, object?>();
                yield return new ChatResponseUpdate
                {
                    Contents = [new FunctionCallContent(tc.Id, tc.Name, args)],
                };
            }

            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.ToolCalls,
            };
        }
        else
        {
            // Stream text in chunks
            const int ChunkSize = 20;
            var text = scripted.Text;

            for (var i = 0; i < text.Length; i += ChunkSize)
            {
                var chunk = text.Substring(i, Math.Min(ChunkSize, text.Length - i));
                yield return new ChatResponseUpdate
                {
                    Contents = [new TextContent(chunk)],
                };
                await Task.Yield();
            }

            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }
    }

    private static IList<AIContent> BuildContents(ScriptedLlmResponse scripted)
    {
        if (scripted.ToolCalls is { Count: > 0 })
        {
            return scripted.ToolCalls
                .Select(tc =>
                {
                    var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.InputJson)
                        ?? new Dictionary<string, object?>();
                    return (AIContent)new FunctionCallContent(tc.Id, tc.Name, args);
                })
                .ToList();
        }

        return [new TextContent(scripted.Text)];
    }
}
