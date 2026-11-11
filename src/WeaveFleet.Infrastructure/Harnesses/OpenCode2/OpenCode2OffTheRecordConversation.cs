using System.Text;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Questions off the record on V2, whose <c>generate</c> calls are one-shot and keep nothing. A follow-up carries the
/// questions and answers before it in its prompt, so it still reads as one conversation. V2 2.2.7 and later report
/// the tokens a call used; before that they're null. <paramref name="delete"/> removes a session made for the questions, once, when it's disposed.
/// </summary>
internal sealed class OpenCode2OffTheRecordConversation(
    Func<string, CancellationToken, Task<OpenCode2GenerateResult?>> generate,
    TimeSpan askTimeout,
    Func<Task>? delete = null) : IOffTheRecordConversation
{
    /// <summary>For a harness that answers with text only.</summary>
    public OpenCode2OffTheRecordConversation(
        Func<string, CancellationToken, Task<string?>> generate,
        TimeSpan askTimeout,
        Func<Task>? delete = null)
        : this(async (prompt, ct) => new OpenCode2GenerateResult { Text = await generate(prompt, ct).ConfigureAwait(false) }, askTimeout, delete)
    {
    }

    private int _disposed;

    private readonly List<(string Question, string Answer)> _earlier = [];

    public async Task<OffTheRecordAnswer?> AskAsync(string prompt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(askTimeout);

        var generated = await generate(WithEarlier(prompt), timeout.Token).ConfigureAwait(false);
        var text = generated?.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        _earlier.Add((prompt, text));
        return new OffTheRecordAnswer(text, generated!.ToOffTheRecordTokens());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && delete is not null)
            await delete().ConfigureAwait(false);
    }

    /// <summary>The questions and answers so far, then this question.</summary>
    internal string WithEarlier(string prompt)
    {
        if (_earlier.Count == 0)
            return prompt;

        var text = new StringBuilder();
        foreach (var (question, answer) in _earlier)
        {
            text.Append(question).Append("\n\n");
            text.Append("Your answer was:\n\n").Append(answer).Append("\n\n");
        }

        return text.Append(prompt).ToString();
    }
}
