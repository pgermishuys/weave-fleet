using System.Text;
using System.Text.Json;

namespace FakeLlmServer;

/// <summary>
/// Maps the OpenAI Chat Completions API endpoint.
/// Implements POST /v1/chat/completions with SSE streaming.
/// </summary>
internal static class OpenAiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static void MapOpenAiEndpoints(this WebApplication app)
    {
        // POST /v1/chat/completions — dequeue next scripted response, stream SSE
        app.MapPost("/v1/chat/completions", async (HttpContext ctx, ScriptedResponseStore queue) =>
        {
            if (!queue.TryDequeue(out var response) || response is null)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("No scripted response queued");
                return;
            }

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var messageId = $"chatcmpl-fake-{Guid.NewGuid():N}";
            var model = "fake-model";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await StreamResponseAsync(ctx.Response, messageId, model, created, response);
        });

        // POST /_control/enqueue — add a scripted response to the queue
        app.MapPost("/_control/enqueue", async (HttpContext ctx, ScriptedResponseStore queue) =>
        {
            var response = await JsonSerializer.DeserializeAsync<ScriptedLlmResponse>(
                ctx.Request.Body, JsonOptions);

            if (response is null)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Invalid response body");
                return;
            }

            queue.Enqueue(response);
            ctx.Response.StatusCode = 204;
        });
    }

    private static async Task StreamResponseAsync(
        HttpResponse response,
        string messageId,
        string model,
        long created,
        ScriptedLlmResponse scripted)
    {
        var ct = response.HttpContext.RequestAborted;

        // Chunk size for text streaming
        const int ChunkSize = 20;

        if (scripted.ToolCalls is { Count: > 0 })
        {
            await StreamToolCallResponseAsync(response, messageId, model, created, scripted, ct);
        }
        else
        {
            await StreamTextResponseAsync(response, messageId, model, created, scripted, ChunkSize, ct);
        }

        // Final [DONE] sentinel
        await WriteSseAsync(response, "[DONE]", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task StreamTextResponseAsync(
        HttpResponse response,
        string messageId,
        string model,
        long created,
        ScriptedLlmResponse scripted,
        int chunkSize,
        CancellationToken ct)
    {
        var text = scripted.Text;

        // First chunk: role delta
        var firstChunk = new
        {
            id = messageId,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content = (string?)null },
                    finish_reason = (string?)null,
                }
            }
        };
        await WriteSseAsync(response, JsonSerializer.Serialize(firstChunk, JsonOptions), ct);

        // Text delta chunks
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
            var deltaChunk = new
            {
                id = messageId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { content = chunk },
                        finish_reason = (string?)null,
                    }
                }
            };
            await WriteSseAsync(response, JsonSerializer.Serialize(deltaChunk, JsonOptions), ct);
        }

        // Final chunk with finish_reason and usage
        var finalChunk = new
        {
            id = messageId,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = scripted.StopReason,
                }
            },
            usage = new
            {
                prompt_tokens = scripted.InputTokens,
                completion_tokens = scripted.OutputTokens,
                total_tokens = scripted.InputTokens + scripted.OutputTokens,
            }
        };
        await WriteSseAsync(response, JsonSerializer.Serialize(finalChunk, JsonOptions), ct);
    }

    private static async Task StreamToolCallResponseAsync(
        HttpResponse response,
        string messageId,
        string model,
        long created,
        ScriptedLlmResponse scripted,
        CancellationToken ct)
    {
        var toolCalls = scripted.ToolCalls!;

        // First chunk: role delta
        var firstChunk = new
        {
            id = messageId,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content = (string?)null },
                    finish_reason = (string?)null,
                }
            }
        };
        await WriteSseAsync(response, JsonSerializer.Serialize(firstChunk, JsonOptions), ct);

        // Tool call chunks — one per tool call
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];

            // Tool call start chunk
            var startChunk = new
            {
                id = messageId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = i,
                                    id = tc.Id,
                                    type = "function",
                                    function = new { name = tc.Name, arguments = "" }
                                }
                            }
                        },
                        finish_reason = (string?)null,
                    }
                }
            };
            await WriteSseAsync(response, JsonSerializer.Serialize(startChunk, JsonOptions), ct);

            // Arguments chunk
            var argsChunk = new
            {
                id = messageId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = i,
                                    function = new { arguments = tc.InputJson }
                                }
                            }
                        },
                        finish_reason = (string?)null,
                    }
                }
            };
            await WriteSseAsync(response, JsonSerializer.Serialize(argsChunk, JsonOptions), ct);
        }

        // Final chunk with tool_calls finish_reason
        var finalChunk = new
        {
            id = messageId,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "tool_calls",
                }
            },
            usage = new
            {
                prompt_tokens = scripted.InputTokens,
                completion_tokens = scripted.OutputTokens,
                total_tokens = scripted.InputTokens + scripted.OutputTokens,
            }
        };
        await WriteSseAsync(response, JsonSerializer.Serialize(finalChunk, JsonOptions), ct);
    }

    private static async Task WriteSseAsync(HttpResponse response, string data, CancellationToken ct)
    {
        var line = $"data: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
