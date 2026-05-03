using System.Text.Json;
using Microsoft.Extensions.AI;
using NuCode.Fakes;
using NuCode.Tools;

namespace NuCode;

public sealed class BatchToolTests
{
    private readonly FakeToolRegistry _toolRegistry = new();
    private readonly BatchTool _sut;

    public BatchToolTests()
    {
        _sut = new BatchTool(_toolRegistry);
    }

    // ── Basic properties ──

    [Fact]
    public void NameIsBatch()
    {
        _sut.Name.ShouldBe("batch");
    }

    [Fact]
    public void ToAIFunctionReturnsFunction()
    {
        var fn = _sut.ToAIFunction();
        fn.ShouldNotBeNull();
        fn.Name.ShouldBe("batch");
    }

    // ── Validation ──

    [Fact]
    public async Task NonArrayInputReturnsError()
    {
        var result = await InvokeRawAsync("\"not-an-array\"");
        result.ShouldContain("must be an array");
    }

    [Fact]
    public async Task MissingToolPropertyReturnsError()
    {
        var result = await InvokeRawAsync("[{\"parameters\": {}}]");
        result.ShouldContain("must have a 'tool' property");
    }

    [Fact]
    public async Task RecursiveBatchCallReturnsError()
    {
        var result = await InvokeRawAsync("[{\"tool\": \"batch\", \"parameters\": {}}]");
        result.ShouldContain("Recursive batch calls are not allowed");
    }

    [Fact]
    public async Task EmptyArrayReturnsError()
    {
        var result = await InvokeRawAsync("[]");
        result.ShouldContain("at least 1 call");
    }

    [Fact]
    public async Task TooManyCallsReturnsError()
    {
        var calls = Enumerable.Range(0, 26)
            .Select(i => new { tool = "read", parameters = new { filePath = $"/file{i}.txt" } });
        var json = JsonSerializer.Serialize(calls);

        var result = await InvokeRawAsync(json);
        result.ShouldContain("at most 25");
    }

    // ── Successful execution ──

    [Fact]
    public async Task SingleSuccessfulCall()
    {
        SetupTool("read", "File contents here");

        var result = await InvokeRawAsync("[{\"tool\": \"read\", \"parameters\": {\"filePath\": \"/test.txt\"}}]");

        result.ShouldContain("1 succeeded, 0 failed");
        result.ShouldContain("read");
        result.ShouldContain("OK");
        result.ShouldContain("File contents here");
    }

    [Fact]
    public async Task MultipleSuccessfulCalls()
    {
        SetupTool("read", "File A");
        SetupTool("glob", "found.txt");

        var json = """
        [
            {"tool": "read", "parameters": {"filePath": "/a.txt"}},
            {"tool": "glob", "parameters": {"pattern": "*.txt"}}
        ]
        """;
        var result = await InvokeRawAsync(json);

        result.ShouldContain("2 succeeded, 0 failed");
        result.ShouldContain("File A");
        result.ShouldContain("found.txt");
    }

    // ── Partial failures ──

    [Fact]
    public async Task UnknownToolReportsFailure()
    {
        SetupTool("read", "OK");

        var json = """
        [
            {"tool": "nonexistent", "parameters": {}},
            {"tool": "read", "parameters": {"filePath": "/test.txt"}}
        ]
        """;
        var result = await InvokeRawAsync(json);

        result.ShouldContain("1 succeeded, 1 failed");
        result.ShouldContain("Unknown tool 'nonexistent'");
        result.Split("---")[^1].ShouldContain("OK"); // last section has the success
    }

    [Fact]
    public async Task ToolExceptionReportsFailure()
    {
        var failFn = AIFunctionFactory.Create(
            string () => throw new InvalidOperationException("tool crashed"),
            new AIFunctionFactoryOptions { Name = "fail" });
        _toolRegistry.Register(new FakeNuCodeTool("fail", "fail", failFn));

        SetupTool("read", "OK");

        var json = """
        [
            {"tool": "fail", "parameters": {}},
            {"tool": "read", "parameters": {"filePath": "/test.txt"}}
        ]
        """;
        var result = await InvokeRawAsync(json);

        result.ShouldContain("1 succeeded, 1 failed");
        result.ShouldContain("FAILED");
    }

    // ── Parameter handling ──

    [Fact]
    public async Task CallWithoutParametersObjectSucceeds()
    {
        SetupTool("read", "result");

        // No 'parameters' key at all — should default to empty dict
        var result = await InvokeRawAsync("[{\"tool\": \"read\"}]");

        result.ShouldContain("1 succeeded");
    }

    [Fact]
    public async Task ParameterTypesConvertedCorrectly()
    {
        // Use a tool that echoes back its parameters
        var echoFn = AIFunctionFactory.Create(
            (string name, long count, bool active) => $"name={name},count={count},active={active}",
            new AIFunctionFactoryOptions { Name = "echo" });
        _toolRegistry.Register(new FakeNuCodeTool("echo", "echo", echoFn));

        var json = """[{"tool": "echo", "parameters": {"name": "test", "count": 42, "active": true}}]""";
        var result = await InvokeRawAsync(json);

        result.ShouldContain("1 succeeded");
        result.ShouldContain("name=test");
        result.ShouldContain("count=42");
        result.ShouldContain("active=True");
    }

    // ── Output format ──

    [Fact]
    public async Task OutputContainsCallIndexAndToolName()
    {
        SetupTool("read", "content");

        var result = await InvokeRawAsync("[{\"tool\": \"read\", \"parameters\": {}}]");

        result.ShouldContain("Call 0 (read)");
    }

    [Fact]
    public async Task BatchCallCaseInsensitivelyBlocked()
    {
        var result = await InvokeRawAsync("[{\"tool\": \"BATCH\", \"parameters\": {}}]");
        result.ShouldContain("Recursive batch calls are not allowed");
    }

    // ── Helpers ──

    private void SetupTool(string name, string returnValue)
    {
        _toolRegistry.Register(new FakeNuCodeTool(name, name, returnValue));
    }

    private async Task<string> InvokeRawAsync(string toolCallsJson)
    {
        var fn = _sut.ToAIFunction();
        var element = JsonSerializer.Deserialize<JsonElement>(toolCallsJson);

        var args = new Dictionary<string, object?>
        {
            ["toolCalls"] = element,
        };

        var result = await fn.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
        return result?.ToString() ?? "";
    }
}
