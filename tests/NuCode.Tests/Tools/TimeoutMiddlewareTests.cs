using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Fakes;
using NuCode.Tools;

namespace NuCode;

public sealed class TimeoutMiddlewareTests
{
    private static IOptionsMonitor<NuCodeConfig> BuildMonitor(TimeoutConfig? timeout = null)
    {
        var config = new NuCodeConfig { Timeout = timeout };
        var options = Microsoft.Extensions.Options.Options.Create(config);
        return new FakeOptionsMonitor(options.Value);
    }

    private static FunctionInvocationContext CreateContext(AIFunction function) =>
        new() { Function = function, Arguments = new AIFunctionArguments(new Dictionary<string, object?>()) };

    private static AIAgent CreateFakeAgent() =>
        new FakeChatClient().AsAIAgent(new ChatClientAgentOptions { Name = "test" });

    // A next delegate that completes immediately
    private static ValueTask<object?> FastNext(FunctionInvocationContext ctx, CancellationToken ct) =>
        ValueTask.FromResult<object?>("ok");

    // A next delegate that delays indefinitely (until cancelled)
    private static async ValueTask<object?> SlowNext(FunctionInvocationContext ctx, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "should not reach";
    }

    [Fact]
    public async Task resolve_timeout_returns_default_when_no_config()
    {
        var ms = TimeoutMiddleware.ResolveTimeoutMs("bash", null);
        ms.ShouldBe(TimeoutMiddleware.DefaultTimeoutMs);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task resolve_timeout_returns_configured_default()
    {
        var config = new TimeoutConfig { DefaultMs = 5000 };
        var ms = TimeoutMiddleware.ResolveTimeoutMs("bash", config);
        ms.ShouldBe(5000);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task resolve_timeout_returns_tool_override()
    {
        var config = new TimeoutConfig
        {
            DefaultMs = 5000,
            ToolOverrides = new Dictionary<string, int> { ["bash"] = 120_000 },
        };
        var ms = TimeoutMiddleware.ResolveTimeoutMs("bash", config);
        ms.ShouldBe(120_000);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task resolve_timeout_falls_back_to_default_for_unregistered_tool()
    {
        var config = new TimeoutConfig
        {
            DefaultMs = 5000,
            ToolOverrides = new Dictionary<string, int> { ["bash"] = 120_000 },
        };
        var ms = TimeoutMiddleware.ResolveTimeoutMs("lsp", config);
        ms.ShouldBe(5000);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task fast_tool_succeeds_without_timeout()
    {
        var monitor = BuildMonitor(new TimeoutConfig { DefaultMs = 5000 });
        var middleware = TimeoutMiddleware.CreateMiddleware(monitor);

        var fn = AIFunctionFactory.Create(() => "result", "fast_tool");
        var context = CreateContext(fn);
        var agent = CreateFakeAgent();

        var result = await middleware(agent, context, FastNext, CancellationToken.None);

        result?.ToString().ShouldBe("ok");
    }

    [Fact]
    public async Task slow_tool_returns_friendly_timeout_message()
    {
        // Very short timeout so test doesn't wait long
        var monitor = BuildMonitor(new TimeoutConfig { DefaultMs = 50 });
        var middleware = TimeoutMiddleware.CreateMiddleware(monitor);

        var fn = AIFunctionFactory.Create(() => "result", "slow_tool");
        var context = CreateContext(fn);
        var agent = CreateFakeAgent();

        var result = await middleware(agent, context, SlowNext, CancellationToken.None);

        var resultStr = result?.ToString() ?? "";
        resultStr.ShouldContain("slow_tool");
        resultStr.ShouldContain("timed out");
        resultStr.ShouldContain("50ms");
    }

    [Fact]
    public async Task per_tool_override_applies_correct_timeout()
    {
        var monitor = BuildMonitor(new TimeoutConfig
        {
            DefaultMs = 5000,
            ToolOverrides = new Dictionary<string, int> { ["slow_tool"] = 50 },
        });
        var middleware = TimeoutMiddleware.CreateMiddleware(monitor);

        var fn = AIFunctionFactory.Create(() => "result", "slow_tool");
        var context = CreateContext(fn);
        var agent = CreateFakeAgent();

        var result = await middleware(agent, context, SlowNext, CancellationToken.None);

        var resultStr = result?.ToString() ?? "";
        resultStr.ShouldContain("timed out");
        resultStr.ShouldContain("50ms");
    }

    [Fact]
    public async Task upstream_cancellation_propagates_without_timeout_message()
    {
        var monitor = BuildMonitor(new TimeoutConfig { DefaultMs = 30_000 });
        var middleware = TimeoutMiddleware.CreateMiddleware(monitor);

        var fn = AIFunctionFactory.Create(() => "result", "tool");
        var context = CreateContext(fn);
        var agent = CreateFakeAgent();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await middleware(agent, context, SlowNext, cts.Token));
    }

    private sealed class FakeOptionsMonitor(NuCodeConfig value) : IOptionsMonitor<NuCodeConfig>
    {
        public NuCodeConfig CurrentValue => value;
        public NuCodeConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<NuCodeConfig, string?> listener) => null;
    }
}
