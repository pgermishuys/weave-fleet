using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class TodoToolTests
{
    private readonly AIFunction _writeFn;
    private readonly AIFunction _readFn;

    public TodoToolTests()
    {
        _writeFn = new TodoWriteTool().ToAIFunction();
        _readFn = new TodoReadTool().ToAIFunction();
    }

    private async Task<string> InvokeWriteAsync(Dictionary<string, object?> args)
    {
        var result = await _writeFn.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    private async Task<string> InvokeReadAsync()
    {
        var result = await _readFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));
        return result?.ToString() ?? "";
    }

    [Fact]
    public void WriteToolNameAndDescriptionAreSet()
    {
        _writeFn.Name.ShouldBe("todowrite");
        _writeFn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public void ReadToolNameAndDescriptionAreSet()
    {
        _readFn.Name.ShouldBe("todoread");
        _readFn.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task WriteToolAcceptsValidTodos()
    {
        var todos = """
        [
            {"content": "Task 1", "status": "pending", "priority": "high"},
            {"content": "Task 2", "status": "completed", "priority": "low"}
        ]
        """;

        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = todos,
        });

        text.ShouldContain("Task 1");
        text.ShouldContain("Task 2");
        text.ShouldContain("pending");
        text.ShouldContain("completed");
    }

    [Fact]
    public async Task WriteToolRejectsInvalidJson()
    {
        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = "not json",
        });

        text.ShouldContain("Error");
        text.ShouldContain("Invalid todos format");
    }

    [Fact]
    public async Task WriteToolRejectsNonArrayJson()
    {
        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = """{"content": "task"}""",
        });

        text.ShouldContain("Error");
        text.ShouldContain("Invalid todos format");
    }

    [Fact]
    public async Task WriteToolRejectsMissingContent()
    {
        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = """[{"status": "pending", "priority": "high"}]""",
        });

        text.ShouldContain("Error");
        text.ShouldContain("content");
    }

    [Fact]
    public async Task WriteToolRejectsInvalidStatus()
    {
        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = """[{"content": "task", "status": "invalid", "priority": "high"}]""",
        });

        text.ShouldContain("Error");
        text.ToLowerInvariant().ShouldContain("invalid status");
    }

    [Fact]
    public async Task WriteToolRejectsInvalidPriority()
    {
        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = """[{"content": "task", "status": "pending", "priority": "urgent"}]""",
        });

        text.ShouldContain("Error");
        text.ToLowerInvariant().ShouldContain("invalid priority");
    }

    [Fact]
    public async Task WriteToolRejectsEmptyInput()
    {
        var text = await InvokeWriteAsync(new Dictionary<string, object?>
        {
            ["todos"] = "",
        });

        text.ShouldContain("Error");
    }

    [Fact]
    public async Task ReadToolReturnsEmptyArray()
    {
        var text = await InvokeReadAsync();

        text.ShouldBe("[]");
    }
}
