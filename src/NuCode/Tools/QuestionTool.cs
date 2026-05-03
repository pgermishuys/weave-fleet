using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Allows the LLM to ask the user questions during execution.
/// Blocks until the host provides an answer via IQuestionService.
/// </summary>
internal sealed class QuestionTool(IQuestionService questionService) : INuCodeTool
{
    public string Name => "question";
    public string Description => "Ask the user a question during execution. Includes a header, question text, and a list of options.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Ask the user a question during execution. Includes a header, question text, and a list of options.")]
    internal async Task<string> ExecuteAsync(
        [Description("A short header/title for the question")] string header,
        [Description("The question text to present to the user")] string question,
        [Description("A list of suggested options for the user to choose from")] JsonElement? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "Error: question is required.";
        }

        var optionsList = ParseOptions(options);
        var sessionId = Sessions.SessionContext.Current ?? new SessionId("unknown");

        var answer = await questionService.AskAsync(
            sessionId,
            header ?? "",
            question,
            optionsList,
            cancellationToken);

        return answer;
    }

    private static List<string> ParseOptions(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind == JsonValueKind.Undefined
            || options.Value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (options.Value.ValueKind == JsonValueKind.Array)
        {
            return options.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }

        return [];
    }
}
