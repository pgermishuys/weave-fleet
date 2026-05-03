using System.Reflection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;
using NuCode.Tools;

namespace NuCode.Lsp;

/// <summary>Integration tests for LSP Phase D features.</summary>
public sealed class LspPhaseDTests : IAsyncLifetime
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

    public LspPhaseDTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_PhaseDTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Task 1: Wire existing Phase B/C features into LspTool ───────────────

    [Fact]
    public async Task get_diagnostics_via_tool_returns_results()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new() { ["operation"] = "getDiagnostics", ["filePath"] = testFile });

        result.ShouldContain("diagnostic");
    }

    [Fact]
    public async Task semantic_tokens_via_tool_returns_data()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class Foo {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new() { ["operation"] = "semanticTokens", ["filePath"] = testFile });

        result.ShouldContain("token");
    }

    [Fact]
    public async Task inlay_hints_via_tool_returns_hints()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;\nfoo(42);");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "inlayHints",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 0,
            ["endLine"] = 2,
            ["endCharacter"] = 0,
        });

        result.ShouldContain("inlay hint");
    }

    [Fact]
    public async Task code_lens_via_tool_returns_lenses()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "function foo() {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new() { ["operation"] = "codeLens", ["filePath"] = testFile });

        result.ShouldContain("code lens");
    }

    [Fact]
    public async Task folding_ranges_via_tool_returns_ranges()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "function foo() {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new() { ["operation"] = "foldingRanges", ["filePath"] = testFile });

        result.ShouldContain("folding range");
    }

    [Fact]
    public async Task selection_ranges_via_tool_returns_ranges()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const myVariable = 42;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "selectionRanges",
            ["filePath"] = testFile,
            ["positions"] = "0:5",
        });

        result.ShouldContain("selection range");
    }

    [Fact]
    public async Task server_status_via_tool_returns_status()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Start the server by making a request
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new() { ["operation"] = "serverStatus" });

        result.ShouldContain("typescript");
        result.ShouldContain("running");
    }

    // ── Task 3/4: New ILspService methods ───────────────────────────────────

    [Fact]
    public async Task go_to_type_definition_returns_location()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.GoToTypeDefinitionAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(10);
        result[0].StartCharacter.ShouldBe(0);
    }

    [Fact]
    public async Task go_to_declaration_returns_location()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.GoToDeclarationAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].StartLine.ShouldBe(5);
        result[0].StartCharacter.ShouldBe(4);
    }

    [Fact]
    public async Task document_highlight_returns_highlights_with_kind()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;\nx = 2;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.DocumentHighlightAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].Kind.ShouldBe(LspDocumentHighlightKind.Read);
        result[0].StartLine.ShouldBe(0);
        result[0].StartCharacter.ShouldBe(6);
        result[1].Kind.ShouldBe(LspDocumentHighlightKind.Write);
        result[1].StartLine.ShouldBe(3);
    }

    [Fact]
    public async Task document_highlight_returns_empty_when_unsupported()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.DocumentHighlightAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task execute_command_returns_result()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Trigger connection first
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        var result = await _manager.ExecuteCommandAsync("test.command", null, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain("Command executed: test.command");
    }

    [Fact]
    public async Task execute_command_returns_null_when_unsupported()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        // Trigger connection first
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        var result = await _manager.ExecuteCommandAsync("test.command", null, CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── Task 6: New features via LspTool ────────────────────────────────────

    [Fact]
    public async Task go_to_type_definition_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "goToTypeDefinition",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 6,
        });

        result.ShouldContain("type definition");
    }

    [Fact]
    public async Task go_to_declaration_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "goToDeclaration",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 6,
        });

        result.ShouldContain("declaration");
    }

    [Fact]
    public async Task document_highlight_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "documentHighlight",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 6,
        });

        result.ShouldContain("highlight");
        result.ShouldContain("Read");
        result.ShouldContain("Write");
    }

    [Fact]
    public async Task execute_command_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Trigger connection first
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "executeCommand",
            ["command"] = "test.command",
        });

        result.ShouldContain("Command executed");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> Invoke(Microsoft.Extensions.AI.AIFunction fn, Dictionary<string, object?> args)
    {
        var result = await fn.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(args));
        return result?.ToString() ?? "";
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
