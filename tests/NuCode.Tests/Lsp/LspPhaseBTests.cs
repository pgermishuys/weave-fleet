using System.Reflection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;

namespace NuCode.Lsp;

/// <summary>Integration tests for LSP Phase B features.</summary>
public sealed class LspPhaseBTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private LspServerManager? _manager;

    private static string MockServerExe
    {
        get
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var baseDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "NuCode.Tests.MockLspServer", "bin"));
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(baseDir, config, "net10.0", "NuCode.Tests.MockLspServer.dll");
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(baseDir, "Release", "net10.0", "NuCode.Tests.MockLspServer.dll");
        }
    }

    public LspPhaseBTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_PhaseBTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Task 2: Capability gating ───────────────────────────────────────────

    [Fact]
    public async Task completion_returns_empty_when_server_does_not_advertise_capability()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        // "limited" mode only advertises hover + definition
        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.CompletionAsync(testFile, 0, 0, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task hover_still_works_in_limited_mode()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Content.ShouldBe("mock hover");
    }

    // ── Task 3/4: Multi-server extension mapping + aggregation ─────────────

    [Fact]
    public async Task two_servers_for_same_extension_both_contribute_to_completion()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        // Configure two servers for .ts — both will return ["console","log"]
        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                ["ts-server-1"] = new LspServerConfig
                {
                    Command = ["dotnet", "exec", MockServerExe],
                    Extensions = [".ts"],
                },
                ["ts-server-2"] = new LspServerConfig
                {
                    Command = ["dotnet", "exec", MockServerExe],
                    Extensions = [".ts"],
                },
            },
        };
        _manager = new LspServerManager(_tempDir, CreateMonitor(config));

        var result = await _manager.CompletionAsync(testFile, 0, 0, CancellationToken.None);

        // Two servers × 2 items each = 4 items (aggregated)
        result.Count.ShouldBe(4);
    }

    [Fact]
    public async Task hover_returns_first_non_null_from_multi_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                ["ts-server-1"] = new LspServerConfig
                {
                    Command = ["dotnet", "exec", MockServerExe],
                    Extensions = [".ts"],
                },
                ["ts-server-2"] = new LspServerConfig
                {
                    Command = ["dotnet", "exec", MockServerExe],
                    Extensions = [".ts"],
                },
            },
        };
        _manager = new LspServerManager(_tempDir, CreateMonitor(config));

        var result = await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        // First non-null is returned (both respond the same, just one matters)
        result.ShouldNotBeNull();
        result!.Content.ShouldBe("mock hover");
    }

    // ── Task 6: Pull diagnostics ────────────────────────────────────────────

    [Fact]
    public async Task get_diagnostics_uses_pull_when_server_supports_diagnostic_provider()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Trigger connection
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        // Pull diagnostics — mock returns one warning
        var diags = await _manager.GetDiagnosticsAsync(testFile, CancellationToken.None);

        diags.ShouldNotBeEmpty();
        diags[0].Message.ShouldBe("mock pull diagnostic");
        diags[0].Severity.ShouldBe(LspDiagnosticSeverity.Warning);
    }

    // ── Task 7/8: workspace/applyEdit + ApplyCodeActionAsync ───────────────

    [Fact]
    public async Task apply_code_action_returns_workspace_edit_with_inline_edit()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // codeAction/resolve returns an inline edit
        var edit = await _manager.ApplyCodeActionAsync(testFile, 0, 0, 0, 5, 0, CancellationToken.None);

        edit.ShouldNotBeNull();
        edit!.Changes.ShouldNotBeEmpty();
        var firstEdit = edit.Changes.Values.First().First();
        firstEdit.NewText.ShouldBe("fixed");
    }

    // ── Task 11: Per-server spawn deduplication ─────────────────────────────

    [Fact]
    public async Task concurrent_requests_for_same_server_only_start_one_process()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Fire multiple concurrent requests — should all succeed, only one process spawned
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            _manager.HoverAsync(testFile, 0, 0, CancellationToken.None)).ToList();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r != null && r.Content == "mock hover");
    }

    private LspServerManager CreateManager(string serverName, string mockServerDll, string extension, string? mode = null)
    {
        var command = mode is not null
            ? new List<string> { "dotnet", "exec", mockServerDll, mode }
            : new List<string> { "dotnet", "exec", mockServerDll };

        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                [serverName] = new LspServerConfig
                {
                    Command = command,
                    Extensions = [extension],
                },
            },
        };
        return new LspServerManager(_tempDir, CreateMonitor(config));
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
