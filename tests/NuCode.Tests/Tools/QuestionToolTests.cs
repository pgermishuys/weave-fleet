using NuCode.Tools;

namespace NuCode;

public sealed class QuestionToolTests
{
    private static readonly SessionId TestSession = new("test-session");

    [Fact]
    public async Task AskBlocksUntilReply()
    {
        var service = new QuestionService();

        var askTask = service.AskAsync(
            TestSession,
            "Choose",
            "Which option?",
            ["A", "B"],
            CancellationToken.None);

        await Task.Delay(50);
        service.GetPendingQuestions().ShouldHaveSingleItem();

        service.ReplyToQuestion(service.GetPendingQuestions()[0].Id, "A");

        var answer = await askTask;
        answer.ShouldBe("A");
        service.GetPendingQuestions().ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationCancelsAsk()
    {
        var service = new QuestionService();
        using var cts = new CancellationTokenSource();

        var askTask = service.AskAsync(
            TestSession,
            "Title",
            "Question?",
            [],
            cts.Token);

        await Task.Delay(50);
        await cts.CancelAsync();

        await Should.ThrowAsync<TaskCanceledException>(() => askTask);
    }

    [Fact]
    public void ReplyToUnknownIdIsNoOp()
    {
        var service = new QuestionService();
        // Should not throw
        service.ReplyToQuestion("nonexistent", "answer");
    }

    [Fact]
    public async Task QuestionRequestContainsCorrectData()
    {
        var service = new QuestionService();

        _ = service.AskAsync(
            TestSession,
            "Header",
            "What do you want?",
            ["Option1", "Option2"],
            CancellationToken.None);

        await Task.Delay(50);
        var pending = service.GetPendingQuestions();
        pending.ShouldHaveSingleItem();
        pending[0].SessionId.ShouldBe(TestSession);
        pending[0].Header.ShouldBe("Header");
        pending[0].Question.ShouldBe("What do you want?");
        pending[0].Options.ShouldBe(new[] { "Option1", "Option2" });

        // Clean up
        service.ReplyToQuestion(pending[0].Id, "done");
    }

    [Fact]
    public async Task ToolReturnsErrorForEmptyQuestion()
    {
        var service = new QuestionService();
        var tool = new QuestionTool(service);
        var fn = tool.ToAIFunction();

        var result = await fn.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["header"] = "h",
                ["question"] = "",
            }));

        (result?.ToString() ?? "").ShouldContain("required");
    }
}
