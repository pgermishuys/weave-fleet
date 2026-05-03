using System.Reflection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;

namespace NuCode.Lsp;

/// <summary>Integration tests for LSP Phase C features.</summary>
public sealed class LspPhaseCTests : IAsyncLifetime
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

    public LspPhaseCTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_PhaseCTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Semantic tokens ─────────────────────────────────────────────────────

    [Fact]
    public async Task semantic_tokens_returns_data_from_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class Foo {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.SemanticTokensAsync(testFile, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Data.Count.ShouldBe(5); // one token = 5 integers
        result.Data[2].ShouldBe(5); // length
        result.ResultId.ShouldBe("mock-1");
    }

    [Fact]
    public async Task semantic_tokens_returns_null_when_unsupported()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.SemanticTokensAsync(testFile, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task semantic_tokens_legend_returns_from_server_capabilities()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class Foo {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Trigger connection so capabilities are available
        await _manager.SemanticTokensAsync(testFile, CancellationToken.None);

        var legend = await _manager.GetSemanticTokensLegendAsync(testFile, CancellationToken.None);

        legend.ShouldNotBeNull();
        legend!.TokenTypes.Count.ShouldBe(5);
        legend.TokenTypes[0].ShouldBe("namespace");
        legend.TokenModifiers.Count.ShouldBe(3);
    }

    // ── Inlay hints ─────────────────────────────────────────────────────────

    [Fact]
    public async Task inlay_hints_returns_hints_for_range()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;\nfoo(42);");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.InlayHintAsync(testFile, 0, 0, 2, 0, CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].Label.ShouldBe(": number");
        result[0].Kind.ShouldBe(LspInlayHintKind.Type);
        result[0].PaddingLeft.ShouldBeTrue();
        result[1].Label.ShouldBe("param:");
        result[1].Kind.ShouldBe(LspInlayHintKind.Parameter);
        result[1].PaddingRight.ShouldBeTrue();
    }

    // ── Code lens ───────────────────────────────────────────────────────────

    [Fact]
    public async Task code_lens_returns_resolved_lenses()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "function foo() {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.CodeLensAsync(testFile, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].IsResolved.ShouldBeTrue();
        result[0].CommandTitle.ShouldBe("1 reference");
        result[0].CommandName.ShouldBe("editor.action.showReferences");
    }

    // ── Folding ranges ──────────────────────────────────────────────────────

    [Fact]
    public async Task folding_ranges_returns_ranges()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "function foo() {\n  // ...\n  // ...\n  // ...\n  // ...\n}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.FoldingRangeAsync(testFile, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(0);
        result[0].EndLine.ShouldBe(5);
        result[0].Kind.ShouldBe("region");
    }

    // ── Selection ranges ────────────────────────────────────────────────────

    [Fact]
    public async Task selection_ranges_returns_ranges_with_parent()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const myVariable = 42;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.SelectionRangeAsync(
            testFile, [(0, 5)], CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartCharacter.ShouldBe(5);
        result[0].EndCharacter.ShouldBe(10);
        result[0].Parent.ShouldNotBeNull();
        result[0].Parent!.StartCharacter.ShouldBe(0);
        result[0].Parent!.EndCharacter.ShouldBe(20);
    }

    // ── Progress reporting ──────────────────────────────────────────────────

    [Fact]
    public async Task progress_events_are_received()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var events = new List<LspProgressValue>();
        _manager.OnProgress(e => events.Add(e));

        // Trigger connection — mock server sends progress during initialized
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        // Allow async notifications to arrive
        await Task.Delay(500);

        events.Count.ShouldBeGreaterThanOrEqualTo(3);
        events.ShouldContain(e => e.Kind == "begin" && e.Title == "Indexing");
        events.ShouldContain(e => e.Kind == "report" && e.Percentage == 50);
        events.ShouldContain(e => e.Kind == "end");
    }

    // ── Server health monitoring ────────────────────────────────────────────

    [Fact]
    public async Task server_status_reflects_running_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Start the server by making a request
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        var statuses = await _manager.GetServerStatusAsync(CancellationToken.None);

        statuses.Count.ShouldBe(1);
        statuses[0].ServerName.ShouldBe("typescript");
        statuses[0].IsRunning.ShouldBeTrue();
        statuses[0].IsFaulted.ShouldBeFalse();
        statuses[0].RestartCount.ShouldBe(0);
        statuses[0].MaxRestarts.ShouldBe(3);
    }

    [Fact]
    public async Task server_status_reflects_faulted_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "crash");

        // The crash mode server exits after initialize — this triggers a faulted state
        // The first request may throw or return null since the server died
        try
        {
            await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);
        }
        catch (IOException)
        {
            // Expected — server exited before we could send the request
        }
        await Task.Delay(500); // Allow process exit detection

        var statuses = await _manager.GetServerStatusAsync(CancellationToken.None);

        statuses.Count.ShouldBe(1);
        statuses[0].IsFaulted.ShouldBeTrue();
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
