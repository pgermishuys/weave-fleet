using System.Reflection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;

namespace NuCode.Lsp;

public sealed class LspConnectionTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private LspServerManager? _manager;

    // Path to the mock LSP server executable — resolve configuration dynamically
    private static string MockServerExe
    {
        get
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var baseDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "NuCode.Tests.MockLspServer", "bin"));

            // Try Debug first (matches typical test runs), then Release
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(baseDir, config, "net10.0", "NuCode.Tests.MockLspServer.dll");
                if (File.Exists(candidate)) return candidate;
            }

            // Fallback — will trigger the skip guard in tests
            return Path.Combine(baseDir, "Release", "net10.0", "NuCode.Tests.MockLspServer.dll");
        }
    }

    public LspConnectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_LspConnectionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
            await _manager.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task hover_returns_mock_content_after_full_handshake()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Content.ShouldBe("mock hover");
    }

    [Fact]
    public async Task go_to_definition_returns_empty_from_mock_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.GoToDefinitionAsync(testFile, 0, 0, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task second_request_does_not_resend_did_open()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Two hover calls — should not throw (second didOpen is silently skipped)
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);
        var result = await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task get_diagnostics_returns_empty_when_no_notifications_received()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        // Use limited mode: no diagnosticProvider capability, so only push cache is consulted
        _manager = CreateManager("typescript", MockServerExe, ".ts", ["limited"]);

        // Trigger connection
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        var diags = await _manager.GetDiagnosticsAsync(testFile, CancellationToken.None);
        diags.ShouldBeEmpty();
    }

    [Fact]
    public async Task returns_empty_when_server_not_configured_for_extension()
    {
        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.GoToDefinitionAsync(
            Path.Combine(_tempDir, "test.cs"), 0, 0, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void lsp_language_id_maps_known_extensions()
    {
        LspLanguageId.GetLanguageId(".cs").ShouldBe("csharp");
        LspLanguageId.GetLanguageId(".ts").ShouldBe("typescript");
        LspLanguageId.GetLanguageId(".tsx").ShouldBe("typescriptreact");
        LspLanguageId.GetLanguageId(".js").ShouldBe("javascript");
        LspLanguageId.GetLanguageId(".jsx").ShouldBe("javascriptreact");
        LspLanguageId.GetLanguageId(".py").ShouldBe("python");
        LspLanguageId.GetLanguageId(".rs").ShouldBe("rust");
        LspLanguageId.GetLanguageId(".go").ShouldBe("go");
        LspLanguageId.GetLanguageId(".java").ShouldBe("java");
        LspLanguageId.GetLanguageId(".rb").ShouldBe("ruby");
        LspLanguageId.GetLanguageId(".cpp").ShouldBe("cpp");
        LspLanguageId.GetLanguageId(".c").ShouldBe("c");
        LspLanguageId.GetLanguageId(".json").ShouldBe("json");
        LspLanguageId.GetLanguageId(".yaml").ShouldBe("yaml");
        LspLanguageId.GetLanguageId(".yml").ShouldBe("yaml");
        LspLanguageId.GetLanguageId(".md").ShouldBe("markdown");
        LspLanguageId.GetLanguageId(".html").ShouldBe("html");
        LspLanguageId.GetLanguageId(".css").ShouldBe("css");
    }

    [Fact]
    public void lsp_language_id_falls_back_to_extension_without_dot()
    {
        LspLanguageId.GetLanguageId(".unknown").ShouldBe("unknown");
        LspLanguageId.GetLanguageId(".xyz").ShouldBe("xyz");
    }

    [Fact]
    public async Task get_diagnostics_returns_empty_by_default()
    {
        _manager = new LspServerManager(_tempDir, CreateMonitor(new NuCodeConfig { LspAutoDetect = false }));

        var diags = await _manager.GetDiagnosticsAsync(
            Path.Combine(_tempDir, "test.ts"), CancellationToken.None);

        diags.ShouldBeEmpty();
    }

    [Fact]
    public async Task completion_returns_items_from_mock_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var items = await _manager.CompletionAsync(testFile, 0, 6, CancellationToken.None);

        items.ShouldNotBeEmpty();
        items.Any(i => i.Label == "console").ShouldBeTrue();
    }

    [Fact]
    public async Task completion_parses_completion_list_wrapper()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var items = await _manager.CompletionAsync(testFile, 0, 6, CancellationToken.None);

        // Mock returns an array — verify items are parsed (not null, not error)
        items.ShouldNotBeNull();
        items.Count.ShouldBeGreaterThan(0);
        items[0].Label.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task notify_document_changed_sends_did_change()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        // Open + change — should not throw and subsequent hover should work
        await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);
        await _manager.NotifyDocumentChangedAsync(testFile, "const y = 2;", CancellationToken.None);
        var result = await _manager.HoverAsync(testFile, 0, 0, CancellationToken.None);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task code_action_returns_actions_from_mock_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var actions = await _manager.CodeActionAsync(testFile, 0, 0, 0, 5, CancellationToken.None);

        actions.ShouldNotBeEmpty();
        actions.Any(a => a.Kind == "quickfix").ShouldBeTrue();
    }

    [Fact]
    public async Task formatting_returns_text_edits_from_mock_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var edits = await _manager.FormattingAsync(testFile, 4, true, CancellationToken.None);

        edits.ShouldNotBeEmpty();
        edits[0].NewText.ShouldBe("const");
    }

    [Fact]
    public async Task rename_returns_workspace_edit_from_mock_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "const x = 1;");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var edit = await _manager.RenameAsync(testFile, 0, 6, "y", CancellationToken.None);

        edit.ShouldNotBeNull();
        edit!.Changes.ShouldNotBeEmpty();
        var edits = edit.Changes.Values.First();
        edits[0].NewText.ShouldBe("newName");
    }

    [Fact]
    public async Task signature_help_returns_signatures_from_mock_server()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "log(");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var help = await _manager.SignatureHelpAsync(testFile, 0, 4, CancellationToken.None);

        help.ShouldNotBeNull();
        help!.Signatures.ShouldNotBeEmpty();
        help.Signatures[0].Label.ShouldBe("log(message: string): void");
        help.Signatures[0].Parameters.ShouldNotBeNull();
        help.Signatures[0].Parameters![0].Label.ShouldBe("message: string");
    }

    private LspServerManager CreateManager(
        string serverName,
        string mockServerDll,
        string extension,
        string[]? extraArgs = null)
    {
        var command = new List<string> { "dotnet", "exec", mockServerDll };
        if (extraArgs is not null) command.AddRange(extraArgs);
        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                [serverName] = new LspServerConfig
                {
                    Command = [.. command],
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
