using Microsoft.Extensions.AI;
using NuCode.Tools;

namespace NuCode;

public sealed class WebSearchToolTests
{
    [Fact]
    public async Task ReturnsFormattedResults()
    {
        var provider = new FakeSearchProvider([
            new WebSearchResult { Title = "Result 1", Url = "https://example.com/1", Snippet = "First result" },
            new WebSearchResult { Title = "Result 2", Url = "https://example.com/2", Snippet = "Second result" },
        ]);

        var tool = new WebSearchTool(provider);
        var fn = tool.ToAIFunction();

        var result = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "test query",
        }));

        var text = result?.ToString() ?? "";
        text.ShouldContain("Result 1");
        text.ShouldContain("https://example.com/1");
        text.ShouldContain("Result 2");
        text.ShouldContain("2 result(s)");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyQuery()
    {
        var provider = new FakeSearchProvider([]);
        var tool = new WebSearchTool(provider);
        var fn = tool.ToAIFunction();

        var result = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "",
        }));

        (result?.ToString() ?? "").ShouldContain("required");
    }

    [Fact]
    public async Task ReturnsNoResultsMessage()
    {
        var provider = new FakeSearchProvider([]);
        var tool = new WebSearchTool(provider);
        var fn = tool.ToAIFunction();

        var result = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "obscure query",
        }));

        (result?.ToString() ?? "").ShouldContain("No results found");
    }

    [Fact]
    public async Task HandlesProviderException()
    {
        var provider = new ThrowingSearchProvider();
        var tool = new WebSearchTool(provider);
        var fn = tool.ToAIFunction();

        var result = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "test",
        }));

        (result?.ToString() ?? "").ShouldContain("Error performing web search");
    }

    [Fact]
    public async Task ClampsCountTo25()
    {
        var provider = new FakeSearchProvider([]);
        var tool = new WebSearchTool(provider);
        var fn = tool.ToAIFunction();

        await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["count"] = 100,
        }));

        provider.LastRequestedCount.ShouldBe(25);
    }

    private sealed class FakeSearchProvider(IReadOnlyList<WebSearchResult> results) : IWebSearchProvider
    {
        public int LastRequestedCount { get; private set; }

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count = 10, CancellationToken cancellationToken = default)
        {
            LastRequestedCount = count;
            return Task.FromResult(results);
        }
    }

    private sealed class ThrowingSearchProvider : IWebSearchProvider
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count = 10, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Network error");
        }
    }
}
