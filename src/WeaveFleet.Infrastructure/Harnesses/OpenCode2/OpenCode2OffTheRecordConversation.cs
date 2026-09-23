using System.Text;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Questions off the record on V2, whose <c>generate</c> calls are one-shot and keep nothing. A follow-up carries the
/// questions and answers before it in its prompt, so it still reads as one conversation. V2 reports no tokens for
/// these, and there's nothing to delete.
/// </summary>
internal sealed class OpenCode2OffTheRecordConversation(
    Func<string, CancellationToken, Task<string?>> generate,
    TimeSpan askTimeout) : IOffTheRecordConversation
{
    private readonly List<(string Question, string Answer)> _earlier = [];

    public async Task<OffTheRecordAnswer?> AskAsync(string prompt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(askTimeout);

        var text = (await generate(WithEarlier(prompt), timeout.Token).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        _earlier.Add((prompt, text));
        return new OffTheRecordAnswer(text, null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
