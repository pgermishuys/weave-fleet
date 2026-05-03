using System.Collections.Concurrent;

namespace NuCode.Tools;

/// <summary>
/// Default implementation of <see cref="IQuestionService"/>.
/// Uses TaskCompletionSource for deferred ask/reply (mirrors PermissionService pattern).
/// </summary>
internal sealed class QuestionService : IQuestionService
{
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();

    public async Task<string> AskAsync(
        SessionId sessionId,
        string header,
        string question,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken = default)
    {
        var requestId = Ulid.NewUlid().ToString();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new QuestionRequest
        {
            Id = requestId,
            SessionId = sessionId,
            Header = header,
            Question = question,
            Options = options,
        };

        var entry = new PendingEntry(request, tcs);
        _pending[requestId] = entry;

        try
        {
            using var registration = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public void ReplyToQuestion(string requestId, string answer)
    {
        if (_pending.TryGetValue(requestId, out var entry))
        {
            entry.Completion.TrySetResult(answer);
        }
    }

    public IReadOnlyList<QuestionRequest> GetPendingQuestions() =>
        _pending.Values.Select(e => e.Request).ToList();

    private sealed record PendingEntry(QuestionRequest Request, TaskCompletionSource<string> Completion);
}
