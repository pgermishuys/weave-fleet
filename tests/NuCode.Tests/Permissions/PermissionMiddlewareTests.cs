using Microsoft.Extensions.AI;
using NuCode.Permissions;

namespace NuCode;

public sealed class PermissionMiddlewareTests
{
    private static readonly SessionId TestSession = new("test-session");

    private static PermissionRuleset AllowGit() => new()
    {
        Name = "allow-git",
        Rules = [new PermissionRule("bash", "git *", PermissionAction.Allow)],
    };

    private static PermissionRuleset DenyRm() => new()
    {
        Name = "deny-rm",
        Rules = [new PermissionRule("bash", "rm *", PermissionAction.Deny)],
    };

    private static PermissionRuleset AskAll() => new()
    {
        Name = "ask-all",
        Rules = [new PermissionRule("bash", "*", PermissionAction.Ask)],
    };

    private sealed class TestExtractor : IPermissionPatternExtractor
    {
        public string Permission => "bash";

        public PermissionPatternResult? ExtractPatterns(string functionName, AIFunctionArguments arguments)
        {
            if (arguments.TryGetValue("command", out var cmd) && cmd is string command)
            {
                var pattern = BashArity.BuildPattern(command);
                return new PermissionPatternResult([pattern], [pattern]);
            }

            return null;
        }
    }

    private static AIFunction CreateTestFunction(string name, Func<string, string> handler)
    {
        return AIFunctionFactory.Create(
            (string command) => handler(command),
            name,
            "A test function");
    }

    private static FunctionInvocationContext CreateContext(AIFunction function, string command)
    {
        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = command }),
        };
    }

    [Fact]
    public async Task AllowedToolProceeds()
    {
        var service = new PermissionService();
        var extractors = new Dictionary<string, IPermissionPatternExtractor>
        {
            ["bash"] = new TestExtractor(),
        };

        var middleware = PermissionMiddleware.Create(service, TestSession, [AllowGit()], extractors);
        var function = CreateTestFunction("bash", cmd => $"executed: {cmd}");
        var context = CreateContext(function, "git status");

        var result = await middleware(context, CancellationToken.None);

        (result?.ToString() ?? "").ShouldContain("executed: git status");
        context.Terminate.ShouldBeFalse();
    }

    [Fact]
    public async Task DeniedToolIsBlocked()
    {
        var service = new PermissionService();
        var extractors = new Dictionary<string, IPermissionPatternExtractor>
        {
            ["bash"] = new TestExtractor(),
        };

        var middleware = PermissionMiddleware.Create(service, TestSession, [DenyRm()], extractors);
        var function = CreateTestFunction("bash", _ => "should not run");
        var context = CreateContext(function, "rm -rf /");

        var result = await middleware(context, CancellationToken.None);

        context.Terminate.ShouldBeTrue();
        (result?.ToString() ?? "").ShouldContain("Permission denied");
    }

    [Fact]
    public async Task AskFlowPausesUntilReply()
    {
        var service = new PermissionService();
        var extractors = new Dictionary<string, IPermissionPatternExtractor>
        {
            ["bash"] = new TestExtractor(),
        };

        var middleware = PermissionMiddleware.Create(service, TestSession, [AskAll()], extractors);
        var function = CreateTestFunction("bash", cmd => $"executed: {cmd}");
        var context = CreateContext(function, "curl http://example.com");

        // Start middleware in background — it will block on ask
        var middlewareTask = Task.Run(async () => await middleware(context, CancellationToken.None));

        await Task.Delay(50);

        // Should have a pending request
        var pending = service.GetPendingRequests();
        pending.ShouldHaveSingleItem();

        // Allow it
        service.ReplyToPermission(pending[0].Id, PermissionDecision.Allow);

        var result = await middlewareTask;
        (result?.ToString() ?? "").ShouldContain("executed: curl http://example.com");
        context.Terminate.ShouldBeFalse();
    }

    [Fact]
    public async Task AskFlowDeniedSetsTerminate()
    {
        var service = new PermissionService();
        var extractors = new Dictionary<string, IPermissionPatternExtractor>
        {
            ["bash"] = new TestExtractor(),
        };

        var middleware = PermissionMiddleware.Create(service, TestSession, [AskAll()], extractors);
        var function = CreateTestFunction("bash", _ => "should not run");
        var context = CreateContext(function, "dangerous-command");

        var middlewareTask = Task.Run(async () => await middleware(context, CancellationToken.None));

        await Task.Delay(50);
        var pending = service.GetPendingRequests();
        service.ReplyToPermission(pending[0].Id, PermissionDecision.Deny);

        var result = await middlewareTask;
        context.Terminate.ShouldBeTrue();
        (result?.ToString() ?? "").ShouldContain("Permission denied");
    }

    [Fact]
    public async Task NoExtractorRegisteredProceedsWithoutCheck()
    {
        var service = new PermissionService();
        var extractors = new Dictionary<string, IPermissionPatternExtractor>();

        var middleware = PermissionMiddleware.Create(service, TestSession, [DenyRm()], extractors);
        var function = CreateTestFunction("unknown_tool", cmd => $"executed: {cmd}");
        var context = CreateContext(function, "anything");

        var result = await middleware(context, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ToString()!.ShouldContain("executed: anything");
        context.Terminate.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractorReturnsNullProceedsWithoutCheck()
    {
        var nullExtractor = new NullExtractor();
        var extractors = new Dictionary<string, IPermissionPatternExtractor>
        {
            ["noarg_tool"] = nullExtractor,
        };
        var service = new PermissionService();

        var middleware = PermissionMiddleware.Create(service, TestSession, [DenyRm()], extractors);
        // Use a function that takes no required params to avoid ArgumentException
        var noArgFunction = AIFunctionFactory.Create(() => "no-arg-result", "noarg_tool", "A tool with no args");
        var context = new FunctionInvocationContext
        {
            Function = noArgFunction,
            Arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["other_arg"] = "value" }),
        };

        var result = await middleware(context, CancellationToken.None);

        // Should proceed because extractor returns null
        result.ShouldNotBeNull();
        result.ToString()!.ShouldContain("no-arg-result");
        context.Terminate.ShouldBeFalse();
    }

    [Theory]
    [InlineData("edit", "edit")]
    [InlineData("write", "edit")]
    [InlineData("apply_patch", "edit")]
    [InlineData("multiedit", "edit")]
    [InlineData("bash", "bash")]
    [InlineData("read", "read")]
    [InlineData("glob", "glob")]
    public void ResolvePermissionTypeMapsCorrectly(string toolName, string expectedPermission)
    {
        var result = PermissionMiddleware.ResolvePermissionType(toolName);

        result.ShouldBe(expectedPermission);
    }

    [Fact]
    public void EditToolNamesContainsAllEditFamilyTools()
    {
        PermissionMiddleware.EditToolNames.ShouldContain("edit");
        PermissionMiddleware.EditToolNames.ShouldContain("write");
        PermissionMiddleware.EditToolNames.ShouldContain("apply_patch");
        PermissionMiddleware.EditToolNames.ShouldContain("multiedit");
    }

    private sealed class NullExtractor : IPermissionPatternExtractor
    {
        public string Permission => "bash";

        public PermissionPatternResult? ExtractPatterns(string functionName, AIFunctionArguments arguments) => null;
    }
}
