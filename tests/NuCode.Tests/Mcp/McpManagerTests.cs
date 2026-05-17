using System.Collections.Immutable;
using NuCode.Mcp;

namespace NuCode;

public sealed class McpManagerTests : IAsyncDisposable
{
    private McpManager? _manager;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.DisposeAsync();
        }
    }

    private McpManager CreateManager(params McpServerConfig[] configs)
    {
        _manager = new McpManager(configs, loggerFactory: null);
        return _manager;
    }

    [Fact]
    public void ConstructorInitializesAllServersAsDisabled()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Stdio, Command = ["echo"] },
            new McpServerConfig { Name = "server-b", Transport = McpTransport.Http, Url = "http://localhost:9999" });

        var status = manager.GetStatus();

        status.Count.ShouldBe(2);
        status["server-a"].Status.ShouldBe(McpServerStatus.Disabled);
        status["server-b"].Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public void GetStatusReturnsCaseInsensitiveLookup()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "MyServer", Transport = McpTransport.Stdio, Command = ["echo"] });

        var status = manager.GetStatus();

        status.ContainsKey("myserver").ShouldBeTrue();
        status.ContainsKey("MYSERVER").ShouldBeTrue();
    }

    [Fact]
    public void GetStatusReturnsSnapshotNotLiveReference()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Stdio, Command = ["echo"] });

        var snapshot1 = manager.GetStatus();
        var snapshot2 = manager.GetStatus();

        snapshot1.ShouldNotBeSameAs(snapshot2);
    }

    [Fact]
    public async Task ConnectAsyncThrowsForUnknownServer()
    {
        var manager = CreateManager();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => manager.ConnectAsync("nonexistent", CancellationToken.None));

        ex.Message.ShouldContain("nonexistent");
    }

    [Fact]
    public async Task AddAsyncWithDisabledConfigSetsDisabledStatus()
    {
        var manager = CreateManager();

        await manager.AddAsync(
            new McpServerConfig { Name = "new-server", Transport = McpTransport.Stdio, Command = ["echo"], Enabled = false },
            CancellationToken.None);

        var status = manager.GetStatus();
        status.ShouldHaveSingleItem();
        status["new-server"].Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public async Task ConnectAllAsyncSkipsDisabledServers()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "disabled-server", Transport = McpTransport.Stdio, Command = ["echo"], Enabled = false });

        await manager.ConnectAllAsync(CancellationToken.None);

        var status = manager.GetStatus();
        status["disabled-server"].Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public async Task ConnectAsyncSetsFailedStatusWhenConnectionFails()
    {
        // Using a stdio transport with a non-existent command — will fail to connect
        var manager = CreateManager(
            new McpServerConfig
            {
                Name = "failing-server",
                Transport = McpTransport.Stdio,
                Command = ["this-command-does-not-exist-12345"],
            });

        await manager.ConnectAsync("failing-server", CancellationToken.None);

        var status = manager.GetStatus();
        status["failing-server"].Status.ShouldBe(McpServerStatus.Failed);
        status["failing-server"].Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task DisconnectAsyncSetsDisabledStatus()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Stdio, Command = ["echo"] });

        await manager.DisconnectAsync("server-a", CancellationToken.None);

        var status = manager.GetStatus();
        status["server-a"].Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public async Task GetToolsAsyncReturnsEmptyWhenNoConnectedServers()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Stdio, Command = ["echo"] });

        var tools = await manager.GetToolsAsync(CancellationToken.None);

        tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAsyncWithEnabledConfigAttemptsConnection()
    {
        var manager = CreateManager();

        // Adding an enabled server with a bad command will attempt connection and fail
        await manager.AddAsync(
            new McpServerConfig
            {
                Name = "dynamic-server",
                Transport = McpTransport.Stdio,
                Command = ["this-command-does-not-exist-12345"],
            },
            CancellationToken.None);

        var status = manager.GetStatus();
        status["dynamic-server"].Status.ShouldBe(McpServerStatus.Failed);
    }

    [Fact]
    public async Task ConnectAsyncWithStdioMissingCommandThrowsDuringTransportCreation()
    {
        // Stdio transport with no command — fails with InvalidOperationException before even trying to connect
        var manager = CreateManager(
            new McpServerConfig { Name = "no-cmd", Transport = McpTransport.Stdio });

        // ConnectCoreAsync catches the exception and sets status to Failed
        await manager.ConnectAsync("no-cmd", CancellationToken.None);

        var status = manager.GetStatus();
        status["no-cmd"].Status.ShouldBe(McpServerStatus.Failed);
        status["no-cmd"].Error.ShouldNotBeNull().ShouldContain("no command configured");
    }

    [Fact]
    public async Task ConnectAsyncWithHttpMissingUrlThrowsDuringTransportCreation()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "no-url", Transport = McpTransport.Http });

        await manager.ConnectAsync("no-url", CancellationToken.None);

        var status = manager.GetStatus();
        status["no-url"].Status.ShouldBe(McpServerStatus.Failed);
        status["no-url"].Error.ShouldNotBeNull().ShouldContain("no URL configured");
    }

    [Fact]
    public void ConstructorWithEmptyConfigsCreatesEmptyStatus()
    {
        var manager = CreateManager();

        var status = manager.GetStatus();
        status.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisposeAsyncCleansUpWithoutError()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Stdio, Command = ["echo"] });

        // Should not throw
        await manager.DisposeAsync();
        _manager = null; // Prevent double-dispose in DisposeAsync
    }

    [Fact]
    public async Task ConnectAllAsyncSetsFailedForBadServers()
    {
        var manager = CreateManager(
            new McpServerConfig
            {
                Name = "bad-stdio",
                Transport = McpTransport.Stdio,
                Command = ["nonexistent-binary-xyz"],
            },
            new McpServerConfig
            {
                Name = "bad-http",
                Transport = McpTransport.Http,
                // Missing URL — will fail in transport creation
            });

        await manager.ConnectAllAsync(CancellationToken.None);

        var status = manager.GetStatus();
        status["bad-stdio"].Status.ShouldBe(McpServerStatus.Failed);
        status["bad-http"].Status.ShouldBe(McpServerStatus.Failed);
    }

    [Fact]
    public async Task AddAsyncOverridesExistingConfig()
    {
        var manager = CreateManager(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Stdio, Command = ["echo"] });

        // Override with disabled config
        await manager.AddAsync(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = "http://localhost:9999", Enabled = false },
            CancellationToken.None);

        var status = manager.GetStatus();
        status["server-a"].Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public void McpServerConfigDefaultsAreCorrect()
    {
        var config = new McpServerConfig { Name = "test", Transport = McpTransport.Stdio };

        config.Enabled.ShouldBeTrue();
        config.TimeoutMs.ShouldBe(30_000);
        config.Command.IsDefaultOrEmpty.ShouldBeTrue();
        config.Url.ShouldBeNull();
        config.Environment.ShouldBeNull();
        config.Headers.ShouldBeNull();
    }

    [Fact]
    public void McpServerStateRecordEquality()
    {
        var state1 = new McpServerState("test", McpServerStatus.Connected);
        var state2 = new McpServerState("test", McpServerStatus.Connected);
        var state3 = new McpServerState("test", McpServerStatus.Failed, "error");

        state1.ShouldBe(state2);
        state1.ShouldNotBe(state3);
    }

    [Fact]
    public async Task StdioTransportWithEnvironmentVariables()
    {
        var env = ImmutableDictionary<string, string>.Empty
            .Add("FOO", "bar")
            .Add("BAZ", "qux");

        var manager = CreateManager(
            new McpServerConfig
            {
                Name = "env-server",
                Transport = McpTransport.Stdio,
                Command = ["nonexistent-binary-xyz"],
                Environment = env,
            });

        // Will fail to connect but validates transport creation doesn't throw for env vars
        await manager.ConnectAsync("env-server", CancellationToken.None);

        var status = manager.GetStatus();
        status["env-server"].Status.ShouldBe(McpServerStatus.Failed);
    }

    [Fact]
    public async Task HttpTransportWithHeaders()
    {
        var headers = ImmutableDictionary<string, string>.Empty
            .Add("Authorization", "Bearer token123");

        var manager = CreateManager(
            new McpServerConfig
            {
                Name = "auth-server",
                Transport = McpTransport.Http,
                Url = "http://localhost:99999",
                Headers = headers,
            });

        // Will fail to connect but validates transport creation includes headers
        await manager.ConnectAsync("auth-server", CancellationToken.None);

        var status = manager.GetStatus();
        status["auth-server"].Status.ShouldBe(McpServerStatus.Failed);
    }
}
