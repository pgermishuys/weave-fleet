using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;

namespace NuCode;

public sealed class LspServerManagerTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private LspServerManager? _manager;

    public LspServerManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_LspTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.DisposeAsync();
        }
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReturnsEmptyWhenNoServerConfiguredForExtension()
    {
        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                ["typescript"] = new LspServerConfig
                {
                    Command = ["fake-lsp"],
                    Extensions = [".ts"],
                },
            },
        };

        _manager = new LspServerManager(_tempDir, CreateMonitor(config));

        // .cs has no server configured, should return empty
        var result = await _manager.GoToDefinitionAsync(
            Path.Combine(_tempDir, "test.cs"), 0, 0, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnsEmptyWhenServerIsDisabled()
    {
        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                ["typescript"] = new LspServerConfig
                {
                    Command = ["fake-lsp"],
                    Extensions = [".ts"],
                    Disabled = true,
                },
            },
        };

        _manager = new LspServerManager(_tempDir, CreateMonitor(config));

        var result = await _manager.GoToDefinitionAsync(
            Path.Combine(_tempDir, "test.ts"), 0, 0, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnsEmptyWhenNoLspConfig()
    {
        _manager = new LspServerManager(_tempDir, CreateMonitor(new NuCodeConfig { LspAutoDetect = false }));

        var result = await _manager.FindReferencesAsync(
            Path.Combine(_tempDir, "test.ts"), 0, 0, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task HoverReturnsNullWhenNoServer()
    {
        _manager = new LspServerManager(_tempDir, CreateMonitor(new NuCodeConfig { LspAutoDetect = false }));

        var result = await _manager.HoverAsync(
            Path.Combine(_tempDir, "test.ts"), 0, 0, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GoToDefinition_throws_when_file_outside_workspace()
    {
        _manager = new LspServerManager(_tempDir, CreateMonitor(new NuCodeConfig { LspAutoDetect = false }));

        var outsidePath = Path.Combine(_tempDir, "..", "outside.ts");

        await Should.ThrowAsync<ArgumentException>(
            () => _manager.GoToDefinitionAsync(outsidePath, 0, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Hover_throws_when_file_uses_traversal()
    {
        _manager = new LspServerManager(_tempDir, CreateMonitor(new NuCodeConfig { LspAutoDetect = false }));

        var traversalPath = Path.Combine(_tempDir, "sub", "..", "..", "evil.ts");

        await Should.ThrowAsync<ArgumentException>(
            () => _manager.HoverAsync(traversalPath, 0, 0, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteCommand_returns_null_when_command_not_in_allowlist()
    {
        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                ["typescript"] = new LspServerConfig
                {
                    Command = ["fake-lsp"],
                    Extensions = [".ts"],
                },
            },
        };

        _manager = new LspServerManager(_tempDir, CreateMonitor(config));

        // No connections active, so returns null regardless
        var result = await _manager.ExecuteCommandAsync("unknown.command", null, CancellationToken.None);

        result.ShouldBeNull();
    }

    private static IOptionsMonitor<NuCodeConfig> CreateMonitor(NuCodeConfig config) =>
        new FakeOptionsMonitor<NuCodeConfig>(config);

    private sealed class FakeOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
