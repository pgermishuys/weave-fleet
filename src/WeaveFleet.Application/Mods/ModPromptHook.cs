using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// What a prompt becomes after the user's mods: the words to show and send, and notes only the model gets. <c>Mod</c> names the
/// mod whose rewrite the words are, when one changed them.
/// </summary>
public sealed record ModPromptOutcome(string Text, IReadOnlyList<string> Context, string? Mod = null)
{
    public static ModPromptOutcome Unchanged(string text) => new(text, []);
}

/// <summary>
/// Runs a prompt through the <c>prompt.submit</c> hooks of the user's mods (<c>docs/mods/api.md</c>, "Events"). It never
/// blocks or breaks a prompt: whatever goes wrong, the prompt goes on as the user typed it.
/// </summary>
public sealed partial class ModPromptHook(IModHost host, TimeProvider time, ILogger<ModPromptHook> logger)
{
    /// <summary>How long Fleet waits for the whole chain. The host gives each hook less (1.5 s).</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    /// <summary>Notes past this many UTF-8 bytes in total are dropped.</summary>
    public const int MaxContextBytes = 8 * 1024;

    public async Task<ModPromptOutcome> ApplyAsync(string userId, string sessionId, string text, CancellationToken ct)
    {
        var unchanged = ModPromptOutcome.Unchanged(text);
        try
        {
            var e = Event(sessionId, text);
            var dispatch = host.DispatchAsync(userId, new ModDispatchRequest("prompt.submit", sessionId, e), ct);
            ModDispatchResult answer;
            try
            {
                answer = await dispatch.WaitAsync(Wait, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The host is left alone; the dispatch finishes on its own and its outcome is dropped.
                _ = dispatch.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                LogTimedOut(Wait.TotalSeconds);
                return unchanged;
            }

            return answer.Dispatched ? Read(answer.Result, unchanged) : unchanged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            return unchanged;
        }
    }

    private ModPromptOutcome Read(JsonElement? result, ModPromptOutcome unchanged)
    {
        if (result is not { ValueKind: JsonValueKind.Object } o)
        {
            LogNotAnObject();
            return unchanged;
        }

        if (!o.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
        {
            LogNoText();
            return unchanged;
        }

        var notes = new List<string>();
        if (o.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Array)
        {
            var bytes = 0;
            foreach (var item in context.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
            {
                var note = item.GetString()!;
                bytes += Encoding.UTF8.GetByteCount(note);
                if (bytes > MaxContextBytes)
                {
                    LogContextCapped(MaxContextBytes);
                    break;
                }

                if (note.Length > 0)
                    notes.Add(note);
            }
        }

        var rewriter = o.TryGetProperty("mod", out var mod) && mod.ValueKind == JsonValueKind.String && mod.GetString() is { Length: > 0 } name ? name : null;
        return new ModPromptOutcome(text.GetString()!, notes, rewriter);
    }

    private static JsonElement Event(string sessionId, string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("text", text);
            writer.WriteStartArray("context");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A prompt.submit hook took longer than {Seconds} s; the prompt goes unchanged.")]
    private partial void LogTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A prompt.submit hook failed; the prompt goes unchanged.")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "prompt.submit answered something other than an object; the prompt goes unchanged.")]
    private partial void LogNotAnObject();

    [LoggerMessage(Level = LogLevel.Warning, Message = "prompt.submit answered without a text; the prompt goes unchanged.")]
    private partial void LogNoText();

    [LoggerMessage(Level = LogLevel.Warning, Message = "prompt.submit context passed {Max} bytes; the rest is dropped.")]
    private partial void LogContextCapped(int max);
}
