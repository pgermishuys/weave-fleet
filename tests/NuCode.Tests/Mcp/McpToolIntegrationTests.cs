using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NuCode.Fakes;
using NuCode.Mcp;
using NuCode.Tools;

namespace NuCode;

public sealed class McpToolIntegrationTests
{
    /// <summary>
    /// Creates a real <see cref="McpClientTool"/> for testing by using a fake <see cref="McpClient"/> subclass.
    /// This avoids the need for a real MCP server connection.
    /// </summary>
    private static McpClientTool CreateTestMcpClientTool(string name, string? description = null)
    {
        var tool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""),
        };

        return new McpClientTool(FakeMcpClient.Instance, tool, JsonSerializerOptions.Default);
    }

    // --- McpToolAdapter tests ---

    [Fact]
    public void AdapterNamePrefixesServerName()
    {
        var mcpTool = CreateTestMcpClientTool("read-file", "Reads a file");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        adapter.Name.ShouldBe("filesystem_read-file");
    }

    [Fact]
    public void AdapterDescriptionUsesToolDescription()
    {
        var mcpTool = CreateTestMcpClientTool("read-file", "Reads a file from disk");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        adapter.Description.ShouldBe("Reads a file from disk");
    }

    [Fact]
    public void AdapterDescriptionFallsBackWhenNull()
    {
        var mcpTool = CreateTestMcpClientTool("read-file");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        // McpClientTool returns empty string for null description, so adapter uses its fallback
        adapter.Description.ShouldContain("read-file");
        adapter.Description.ShouldContain("filesystem");
    }

    [Fact]
    public void AdapterServerNameIsSet()
    {
        var mcpTool = CreateTestMcpClientTool("read-file", "Reads a file");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        adapter.ServerName.ShouldBe("filesystem");
    }

    [Fact]
    public void AdapterToAIFunctionReturnsRenamedTool()
    {
        var mcpTool = CreateTestMcpClientTool("read-file", "Reads a file");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        var aiFunction = adapter.ToAIFunction();

        aiFunction.ShouldNotBeNull();
        aiFunction.Name.ShouldBe("filesystem_read-file");
    }

    [Fact]
    public void AdapterToAIFunctionReturnsMcpClientTool()
    {
        var mcpTool = CreateTestMcpClientTool("read-file", "Reads a file");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        var aiFunction = adapter.ToAIFunction();

        aiFunction.ShouldBeOfType<McpClientTool>();
    }

    [Fact]
    public void AdapterToAIFunctionPreservesDescription()
    {
        var mcpTool = CreateTestMcpClientTool("read-file", "Reads a file");
        var adapter = new McpToolAdapter(mcpTool, "filesystem");

        var aiFunction = adapter.ToAIFunction();

        aiFunction.Description.ShouldBe("Reads a file");
    }

    // --- McpToolRegistration tests ---

    [Fact]
    public async Task RegisterToolsAsyncReturnsZeroWhenNoServersConfigured()
    {
        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>());
        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RegisterToolsAsync(CancellationToken.None);

        count.ShouldBe(0);
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterToolsAsyncCallsConnectAll()
    {
        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>());
        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        await registration.RegisterToolsAsync(CancellationToken.None);

        mcpManager.ConnectAllCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task RegisterToolsAsyncRegistersToolsFromMcpServer()
    {
        var tool1 = CreateTestMcpClientTool("read-file", "Read a file");
        var tool2 = CreateTestMcpClientTool("write-file", "Write a file");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            ["filesystem"] = new List<AITool> { tool1, tool2 }.AsReadOnly(),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RegisterToolsAsync(CancellationToken.None);

        count.ShouldBe(2);
        registry.Get("filesystem_read-file").ShouldNotBeNull();
        registry.Get("filesystem_write-file").ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterToolsAsyncRegistersToolsFromMultipleServers()
    {
        var tool1 = CreateTestMcpClientTool("search", "Search docs");
        var tool2 = CreateTestMcpClientTool("fetch", "Fetch URL");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            ["docs"] = new List<AITool> { tool1 }.AsReadOnly(),
            ["web"] = new List<AITool> { tool2 }.AsReadOnly(),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RegisterToolsAsync(CancellationToken.None);

        count.ShouldBe(2);
        registry.Get("docs_search").ShouldNotBeNull();
        registry.Get("web_fetch").ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterToolsAsyncSkipsNonMcpClientTools()
    {
        // Create a plain AITool (not McpClientTool) — should be skipped
        var plainTool = AIFunctionFactory.Create(() => "result", "plain-tool", "A plain tool");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            ["server"] = new List<AITool> { plainTool }.AsReadOnly(),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RegisterToolsAsync(CancellationToken.None);

        count.ShouldBe(0);
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterToolsAsyncHandlesDuplicateToolNames()
    {
        // Same tool name from two servers — second registration will throw InvalidOperationException
        // but should be caught and logged
        var tool1 = CreateTestMcpClientTool("read", "Read from server1");
        var tool2 = CreateTestMcpClientTool("read", "Read from server2");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            // Both servers have "read" tool — but prefixed differently, so no conflict
            ["server1"] = new List<AITool> { tool1 }.AsReadOnly(),
            ["server2"] = new List<AITool> { tool2 }.AsReadOnly(),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RegisterToolsAsync(CancellationToken.None);

        // Both should register because names are "server1_read" and "server2_read"
        count.ShouldBe(2);
    }

    [Fact]
    public async Task RegisterToolsAsyncHandlesSameServerSameToolNameConflict()
    {
        // What if the same server returns two tools with the same name?
        // This shouldn't happen in practice but the code should handle it gracefully
        var tool1 = CreateTestMcpClientTool("read", "Read v1");
        var tool2 = CreateTestMcpClientTool("read", "Read v2");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            ["server"] = new List<AITool> { tool1, tool2 }.AsReadOnly(),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        // Should not throw — the duplicate is caught by the try/catch
        var count = await registration.RegisterToolsAsync(CancellationToken.None);

        count.ShouldBe(1); // Only first registers
        registry.Get("server_read").ShouldNotBeNull();
    }

    // --- McpToolRegistration.RefreshServerToolsAsync tests ---

    [Fact]
    public async Task RefreshServerToolsAsyncReturnsZeroForDisconnectedServer()
    {
        var mcpManager = new FakeMcpManager();
        mcpManager.SetStatus(new Dictionary<string, McpServerState>
        {
            ["server"] = new McpServerState("server", McpServerStatus.Disabled),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RefreshServerToolsAsync("server", CancellationToken.None);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshServerToolsAsyncReturnsZeroForUnknownServer()
    {
        var mcpManager = new FakeMcpManager();
        mcpManager.SetStatus(new Dictionary<string, McpServerState>());

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RefreshServerToolsAsync("unknown", CancellationToken.None);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshServerToolsAsyncRegistersNewTools()
    {
        var tool = CreateTestMcpClientTool("new-tool", "A new tool");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetStatus(new Dictionary<string, McpServerState>
        {
            ["server"] = new McpServerState("server", McpServerStatus.Connected),
        });
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            ["server"] = new List<AITool> { tool }.AsReadOnly(),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RefreshServerToolsAsync("server", CancellationToken.None);

        count.ShouldBe(1);
        registry.Get("server_new-tool").ShouldNotBeNull();
    }

    [Fact]
    public async Task RefreshServerToolsAsyncSkipsAlreadyRegisteredTools()
    {
        var tool = CreateTestMcpClientTool("existing-tool", "Already registered");

        var mcpManager = new FakeMcpManager();
        mcpManager.SetStatus(new Dictionary<string, McpServerState>
        {
            ["server"] = new McpServerState("server", McpServerStatus.Connected),
        });
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>
        {
            ["server"] = new List<AITool> { tool }.AsReadOnly(),
        });

        // Pre-register the tool
        var registry = new ToolRegistry();
        var existingAdapter = new McpToolAdapter(tool, "server");
        registry.Register(existingAdapter);

        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RefreshServerToolsAsync("server", CancellationToken.None);

        count.ShouldBe(0); // No new tools registered
    }

    [Fact]
    public async Task RefreshServerToolsAsyncReturnsZeroWhenServerNotInToolList()
    {
        var mcpManager = new FakeMcpManager();
        mcpManager.SetStatus(new Dictionary<string, McpServerState>
        {
            ["server"] = new McpServerState("server", McpServerStatus.Connected),
        });
        mcpManager.SetToolsByServer(new Dictionary<string, IReadOnlyList<AITool>>()); // Empty — server not found in tools

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RefreshServerToolsAsync("server", CancellationToken.None);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshServerToolsAsyncReturnsZeroForFailedServer()
    {
        var mcpManager = new FakeMcpManager();
        mcpManager.SetStatus(new Dictionary<string, McpServerState>
        {
            ["server"] = new McpServerState("server", McpServerStatus.Failed, "connection error"),
        });

        var registry = new ToolRegistry();
        var registration = new McpToolRegistration(mcpManager, registry, loggerFactory: null);

        var count = await registration.RefreshServerToolsAsync("server", CancellationToken.None);

        count.ShouldBe(0);
    }

    // --- DI registration tests ---

    [Fact]
    public void McpManagerIsResolvableFromDI()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IMcpManager>();

        manager.ShouldNotBeNull();
    }

    [Fact]
    public void McpManagerIsSingletonFromDI()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var manager1 = provider.GetRequiredService<IMcpManager>();
        var manager2 = provider.GetRequiredService<IMcpManager>();

        manager2.ShouldBeSameAs(manager1);
    }

    [Fact]
    public void McpToolRegistrationIsResolvableFromDI()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<McpToolRegistration>();

        registration.ShouldNotBeNull();
    }

    [Fact]
    public void McpServersConfigPassedThroughToManager()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddNuCode(opts =>
        {
            opts.McpServers.Add(new McpServerConfig
            {
                Name = "test-server",
                Transport = McpTransport.Stdio,
                Command = ["echo"],
            });
        });

        var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IMcpManager>();
        var status = manager.GetStatus();

        status.ShouldHaveSingleItem();
        status.ContainsKey("test-server").ShouldBeTrue();
    }

    /// <summary>
    /// Minimal fake implementation of <see cref="McpClient"/> for constructing <see cref="McpClientTool"/> in tests.
    /// The <see cref="McpClientTool"/> constructor requires a non-null <see cref="McpClient"/> reference,
    /// but never calls any of its methods during construction or <c>WithName</c>/<c>WithDescription</c> calls.
    /// </summary>
#pragma warning disable MCPEXP002 // McpClient constructor is experimental — acceptable in test code
    private sealed class FakeMcpClient : McpClient
#pragma warning restore MCPEXP002
    {
        public static readonly FakeMcpClient Instance = new();

        public override ServerCapabilities ServerCapabilities => new();
        public override Implementation ServerInfo => new() { Name = "fake", Version = "1.0" };
        public override string? ServerInstructions => null;
        public override Task<ClientCompletionDetails> Completion =>
            Task.FromResult(new ClientCompletionDetails());
        public override string SessionId => "fake-session";
        public override string NegotiatedProtocolVersion => "2024-11-05";

        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("FakeMcpClient does not support sending requests.");

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken) =>
            throw new NotSupportedException("FakeMcpClient does not support sending messages.");

        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler) =>
            throw new NotSupportedException("FakeMcpClient does not support notification handlers.");

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
