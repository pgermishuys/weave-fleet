using global::NuCode;
using global::NuCode.Tools;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// An <see cref="IQuestionService"/> that immediately denies all questions.
/// Used when NuCode runs as a WeaveFleet harness — interactive Q&amp;A is not supported
/// in the orchestrator context. The agent receives a denial and should continue without
/// waiting for user input.
/// </summary>
internal sealed class DenyAllQuestionService : IQuestionService
{
    private const string DenialMessage =
        "Questions are not supported in this context. Please proceed without asking the user.";

    /// <inheritdoc />
    public Task<string> AskAsync(
        SessionId sessionId,
        string header,
        string question,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DenialMessage);
    }

    /// <inheritdoc />
    public void ReplyToQuestion(string requestId, string answer)
    {
        // No-op — questions are never pending in deny-all mode.
    }

    /// <inheritdoc />
    public IReadOnlyList<QuestionRequest> GetPendingQuestions() => [];
}
