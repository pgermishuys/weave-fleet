using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NuCode.Mcp;

namespace NuCode;

public sealed class McpReconnectTests : IAsyncDisposable
{
    private McpManager? _manager;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.DisposeAsync();
        }
    }

    private McpManager CreateManager(FakeClientFactory factory, params McpServerConfig[] configs)
    {
        _manager = new McpManager(configs, factory, loggerFactory: null);
        return _manager;
    }

    private static McpServerConfig DefaultConfig(
        string name = "test-server",
        bool autoReconnect = true,
        int maxRestarts = 3)
    {
        return new McpServerConfig
        {
            Name = name,
            Transport = McpTransport.Stdio,
            Command = ["echo"],
            AutoReconnect = autoReconnect,
            MaxRestarts = maxRestarts,
        };
    }

    [Fact]
    public void McpServerConfig_defaults_include_reconnect_settings()
    {
        var config = new McpServerConfig { Name = "test", Transport = McpTransport.Stdio };

        config.AutoReconnect.ShouldBeTrue();
        config.MaxRestarts.ShouldBe(3);
    }

    [Fact]
    public void McpServerState_includes_restart_tracking_defaults()
    {
        var state = new McpServerState("test", McpServerStatus.Connected);

        state.RestartCount.ShouldBe(0);
        state.MaxRestarts.ShouldBe(3);
    }

    [Fact]
    public void McpServerState_with_explicit_restart_tracking()
    {
        var state = new McpServerState("test", McpServerStatus.Reconnecting, null, 2, 5);

        state.RestartCount.ShouldBe(2);
        state.MaxRestarts.ShouldBe(5);
    }

    [Fact]
    public void McpServerStatus_has_Reconnecting_value()
    {
        McpServerStatus.Reconnecting.ShouldNotBe(McpServerStatus.Connected);
        McpServerStatus.Reconnecting.ShouldNotBe(McpServerStatus.Failed);
        McpServerStatus.Reconnecting.ShouldNotBe(McpServerStatus.Disabled);
    }

    [Fact]
    public async Task ConnectAsync_successful_sets_Connected_status()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());

        await manager.ConnectAsync("test-server", CancellationToken.None);

        manager.GetStatus()["test-server"].Status.ShouldBe(McpServerStatus.Connected);
    }

    [Fact]
    public async Task ConnectAsync_failure_sets_Failed_status()
    {
        var factory = new FakeClientFactory { ShouldFail = true };
        var manager = CreateManager(factory, DefaultConfig());

        await manager.ConnectAsync("test-server", CancellationToken.None);

        manager.GetStatus()["test-server"].Status.ShouldBe(McpServerStatus.Failed);
    }

    [Fact]
    public async Task ConnectAsync_failure_includes_error_message()
    {
        var factory = new FakeClientFactory { ShouldFail = true, FailureMessage = "Connection refused" };
        var manager = CreateManager(factory, DefaultConfig());

        await manager.ConnectAsync("test-server", CancellationToken.None);

        var status = manager.GetStatus();
        status["test-server"].Error.ShouldNotBeNull();
        status["test-server"].Error!.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task ServerStateChanged_fires_on_connect()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());
        var states = new List<McpServerState>();
        manager.ServerStateChanged += s => states.Add(s);

        await manager.ConnectAsync("test-server", CancellationToken.None);

        states.ShouldContain(s => s.Status == McpServerStatus.Connected);
    }

    [Fact]
    public async Task ServerStateChanged_fires_on_failure()
    {
        var factory = new FakeClientFactory { ShouldFail = true };
        var manager = CreateManager(factory, DefaultConfig());
        var states = new List<McpServerState>();
        manager.ServerStateChanged += s => states.Add(s);

        await manager.ConnectAsync("test-server", CancellationToken.None);

        states.ShouldContain(s => s.Status == McpServerStatus.Failed);
    }

    [Fact]
    public async Task ServerStateChanged_fires_on_disconnect()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());
        await manager.ConnectAsync("test-server", CancellationToken.None);

        var states = new List<McpServerState>();
        manager.ServerStateChanged += s => states.Add(s);

        await manager.DisconnectAsync("test-server", CancellationToken.None);

        states.ShouldContain(s => s.Status == McpServerStatus.Disabled);
    }

    [Fact]
    public async Task DisconnectAsync_disposes_client()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());
        await manager.ConnectAsync("test-server", CancellationToken.None);

        var client = factory.CreatedClients[0];
        await manager.DisconnectAsync("test-server", CancellationToken.None);

        client.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckHealthAsync_returns_Connected_when_ping_succeeds()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());
        await manager.ConnectAsync("test-server", CancellationToken.None);

        var result = await manager.CheckHealthAsync("test-server", CancellationToken.None);

        result.Status.ShouldBe(McpServerStatus.Connected);
    }

    [Fact]
    public async Task CheckHealthAsync_throws_for_unknown_server()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory);

        await Should.ThrowAsync<InvalidOperationException>(
            () => manager.CheckHealthAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public async Task CheckHealthAsync_returns_Disabled_for_non_connected_server()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());

        var result = await manager.CheckHealthAsync("test-server", CancellationToken.None);

        result.Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public async Task CheckHealthAsync_triggers_reconnect_on_ping_failure()
    {
        var factory = new FakeClientFactory();
        var config = DefaultConfig(autoReconnect: true, maxRestarts: 1);
        var manager = CreateManager(factory, config);
        await manager.ConnectAsync("test-server", CancellationToken.None);

        // Make ping fail
        factory.CreatedClients[0].ShouldFailPing = true;

        await manager.CheckHealthAsync("test-server", CancellationToken.None);

        // Give the background reconnect task time (backoff is 2^1 = 2s)
        await Task.Delay(3000);

        // Factory should have been called again for reconnect
        factory.CreateCallCount.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task AutoReconnect_false_sets_Failed_on_health_check_failure()
    {
        var factory = new FakeClientFactory();
        var config = DefaultConfig(autoReconnect: false);
        var manager = CreateManager(factory, config);
        await manager.ConnectAsync("test-server", CancellationToken.None);

        factory.CreatedClients[0].ShouldFailPing = true;

        var states = new List<McpServerState>();
        manager.ServerStateChanged += s => states.Add(s);

        await manager.CheckHealthAsync("test-server", CancellationToken.None);
        await Task.Delay(200);

        states.ShouldContain(s => s.Status == McpServerStatus.Failed);
        states.ShouldNotContain(s => s.Status == McpServerStatus.Reconnecting);
        factory.CreateCallCount.ShouldBe(1); // No reconnect attempt
    }

    [Fact]
    public async Task GetStatus_includes_RestartCount_and_MaxRestarts()
    {
        var factory = new FakeClientFactory();
        var config = DefaultConfig(maxRestarts: 5);
        var manager = CreateManager(factory, config);
        await manager.ConnectAsync("test-server", CancellationToken.None);

        var status = manager.GetStatus();
        status["test-server"].RestartCount.ShouldBe(0);
        status["test-server"].MaxRestarts.ShouldBe(5);
    }

    [Fact]
    public async Task ConnectAllAsync_skips_disabled_servers()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(
            factory,
            new McpServerConfig { Name = "disabled", Transport = McpTransport.Stdio, Command = ["echo"], Enabled = false });

        await manager.ConnectAllAsync(CancellationToken.None);

        factory.CreateCallCount.ShouldBe(0);
        manager.GetStatus()["disabled"].Status.ShouldBe(McpServerStatus.Disabled);
    }

    [Fact]
    public async Task Concurrent_reconnects_are_deduplicated()
    {
        var factory = new FakeClientFactory { CreateDelay = TimeSpan.FromMilliseconds(500) };
        var config = DefaultConfig(autoReconnect: true, maxRestarts: 3);
        var manager = CreateManager(factory, config);
        await manager.ConnectAsync("test-server", CancellationToken.None);

        factory.CreatedClients[0].ShouldFailPing = true;

        // Trigger two concurrent reconnects via CheckHealthAsync
        var t1 = manager.CheckHealthAsync("test-server", CancellationToken.None);
        var t2 = manager.CheckHealthAsync("test-server", CancellationToken.None);
        await Task.WhenAll(t1, t2);

        // Wait for the background reconnect to complete (2^1 = 2s backoff + 500ms create delay)
        await Task.Delay(3500);

        // Only one reconnect should have been attempted (initial connect + 1 reconnect)
        factory.CreateCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task MaxRestarts_is_clamped_to_10()
    {
        var factory = new FakeClientFactory();
        var config = DefaultConfig(maxRestarts: 100);
        var manager = CreateManager(factory, config);
        await manager.ConnectAsync("test-server", CancellationToken.None);

        var status = manager.GetStatus();
        // The clamped value should be reflected in state
        status["test-server"].MaxRestarts.ShouldBe(10);
    }

    [Fact]
    public async Task ServerStateChanged_exception_does_not_crash_connect()
    {
        var factory = new FakeClientFactory();
        var manager = CreateManager(factory, DefaultConfig());
        manager.ServerStateChanged += _ => throw new InvalidOperationException("Bad subscriber");

        // Should not throw despite the bad subscriber
        await manager.ConnectAsync("test-server", CancellationToken.None);

        manager.GetStatus()["test-server"].Status.ShouldBe(McpServerStatus.Connected);
    }

    /// <summary>
    /// Fake <see cref="IMcpClientWrapper"/> for testing.
    /// </summary>
    internal sealed class FakeMcpClient : IMcpClientWrapper
    {
        public bool ShouldFailPing { get; set; }
        public bool Disposed { get; private set; }

        public Task PingAsync(CancellationToken cancellationToken)
        {
            if (ShouldFailPing)
            {
                throw new InvalidOperationException("Ping failed (fake)");
            }

            return Task.CompletedTask;
        }

        public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IList<McpClientTool>>([]);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Fake <see cref="IMcpClientFactory"/> that produces <see cref="FakeMcpClient"/> instances.
    /// </summary>
    internal sealed class FakeClientFactory : IMcpClientFactory
    {
        private readonly List<FakeMcpClient> _createdClients = [];

        public bool ShouldFail { get; set; }
        public string FailureMessage { get; set; } = "Fake connection failure";
        public TimeSpan CreateDelay { get; set; } = TimeSpan.Zero;
        public int CreateCallCount { get; private set; }
        public IReadOnlyList<FakeMcpClient> CreatedClients => _createdClients;

        public async Task<IMcpClientWrapper> CreateAsync(
            McpServerConfig config,
            ILoggerFactory? loggerFactory,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;

            if (CreateDelay > TimeSpan.Zero)
            {
                await Task.Delay(CreateDelay, cancellationToken);
            }

            if (ShouldFail)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            var client = new FakeMcpClient();
            _createdClients.Add(client);
            return client;
        }
    }
}
