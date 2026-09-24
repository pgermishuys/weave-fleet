using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Questions asked in a throwaway OpenCode session: a fork of a Fleet session, or a session made for this alone. Every
/// question is sent the same way (<paramref name="template"/> with its text), so a follow-up reads the earlier ones from
/// the provider's cache. Disposing it deletes the session, once, whatever happened before.
/// </summary>
internal sealed class OpenCodeOffTheRecordConversation(
    OpenCodeHttpClient http,
    string sessionId,
    string directory,
    OpenCodePromptRequest template,
    TimeSpan askTimeout,
    Func<string, Task> delete) : IOffTheRecordConversation
{
    private int _disposed;

    /// <summary>The OpenCode session the questions go to.</summary>
    internal string SessionId => sessionId;

    public async Task<OffTheRecordAnswer?> AskAsync(string prompt, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(askTimeout);

        var answer = await http.SendMessageAsync(
            sessionId,
            template with { Parts = [new OpenCodePromptTextPart { Text = prompt }] },
            directory,
            timeout.Token).ConfigureAwait(false);

        var text = string.Concat((answer?.Parts ?? [])
            .OfType<OpenCodeTextPart>()
            .Where(p => p.Synthetic != true && p.Ignored != true)
            .Select(p => p.Text)).Trim();
        return text.Length > 0 ? new OffTheRecordAnswer(text, Tokens(answer?.Info as OpenCodeAssistantMessage)) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await delete(sessionId).ConfigureAwait(false);
    }

    private static OffTheRecordTokens? Tokens(OpenCodeAssistantMessage? message)
    {
        if (message?.Tokens is not { } tokens)
            return null;

        var cacheRead = (long)(tokens.Cache?.Read ?? 0);
        var cacheWrite = (long)(tokens.Cache?.Write ?? 0);
        var total = (long)(tokens.Total ?? tokens.Input + tokens.Output + tokens.Reasoning + cacheRead + cacheWrite);
        return total > 0 ? new OffTheRecordTokens(total, cacheRead) : null;
    }
}
