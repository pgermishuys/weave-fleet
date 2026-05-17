using NuCode.Tools;

namespace NuCode.Events;

/// <summary>
/// Question-related events.
/// </summary>
public static class QuestionEvents
{
    /// <summary>Properties for question asked events.</summary>
    public sealed record QuestionAskedInfo(
        string RequestId,
        SessionId SessionId,
        string Header,
        string Question,
        IReadOnlyList<string> Options);

    /// <summary>A question was asked and is awaiting user response.</summary>
    public static readonly NuCodeEventDefinition<QuestionAskedInfo> Asked = new("question.asked");

    /// <summary>Properties for question answered events.</summary>
    public sealed record QuestionAnsweredInfo(
        string RequestId,
        SessionId SessionId,
        string Answer);

    /// <summary>A user answered a question.</summary>
    public static readonly NuCodeEventDefinition<QuestionAnsweredInfo> Answered = new("question.answered");
}
