namespace NuCode.Tools;

/// <summary>
/// Manages question requests from the LLM to the user.
/// Uses a deferred ask/reply pattern where the LLM blocks until the host responds.
/// </summary>
public interface IQuestionService
{
    /// <summary>
    /// Asks the user a question and blocks until the host replies.
    /// </summary>
    /// <param name="sessionId">The current session.</param>
    /// <param name="header">A header/title for the question.</param>
    /// <param name="question">The question text.</param>
    /// <param name="options">Suggested options for the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's answer.</returns>
    Task<string> AskAsync(
        SessionId sessionId,
        string header,
        string question,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replies to a pending question request.
    /// </summary>
    /// <param name="requestId">The ID of the pending question.</param>
    /// <param name="answer">The user's answer.</param>
    void ReplyToQuestion(string requestId, string answer);

    /// <summary>
    /// Gets all pending question requests.
    /// </summary>
    IReadOnlyList<QuestionRequest> GetPendingQuestions();
}
