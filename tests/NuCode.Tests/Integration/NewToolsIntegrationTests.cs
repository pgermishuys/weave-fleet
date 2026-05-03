using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NuCode.Lsp;
using NuCode.Sessions;
using NuCode.Tools;

namespace NuCode.Integration;

/// <summary>
/// Integration tests verifying all 5 new tools are properly registered in the DI container
/// and functional through the full pipeline.
/// </summary>
public sealed class NewToolsIntegrationTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider;
    private readonly string _tempDir;

    public NewToolsIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nucode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = _tempDir;
        });

        // Register a mock IWebSearchProvider so WebSearchTool gets registered
        services.AddSingleton<IWebSearchProvider>(new FakeWebSearchProvider());

        // Register a mock ILspService
        services.AddSingleton<ILspService>(new FakeLspService());

        _provider = services.BuildServiceProvider();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private IToolRegistry ToolRegistry => _provider.GetRequiredService<IToolRegistry>();

    private static async Task<string> InvokeTool(INuCodeTool tool, Dictionary<string, object?> args)
    {
        var fn = tool.ToAIFunction();
        var result = await fn.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    [Fact]
    public void All_new_tools_are_registered()
    {
        var names = ToolRegistry.GetAll().Select(t => t.Name).ToHashSet();

        names.ShouldContain("apply_patch");
        names.ShouldContain("skill");
        names.ShouldContain("question");
        names.ShouldContain("websearch");
        names.ShouldContain("lsp");
    }

    [Fact]
    public void IQuestionService_is_resolvable()
    {
        _provider.GetService<IQuestionService>().ShouldNotBeNull();
    }

    [Fact]
    public void ISkillProvider_is_resolvable()
    {
        _provider.GetService<ISkillProvider>().ShouldNotBeNull();
    }

    [Fact]
    public async Task ApplyPatchTool_creates_file_in_temp_directory()
    {
        var tool = ToolRegistry.Get("apply_patch")!;
        var targetFile = Path.Combine(_tempDir, "newfile.txt");
        var patch = $"*** Add File: {targetFile}\nhello world\n";

        var result = await InvokeTool(tool, new() { ["patchText"] = patch });
        result.ShouldContain("Added");
        File.Exists(targetFile).ShouldBeTrue();
    }

    [Fact]
    public async Task QuestionTool_blocks_until_answer_provided()
    {
        var questionService = _provider.GetRequiredService<IQuestionService>();
        var tool = ToolRegistry.Get("question")!;

        SessionContext.Set(new SessionId("test-session"));
        try
        {
            var invokeTask = InvokeTool(tool, new()
            {
                ["header"] = "Color choice",
                ["question"] = "Pick a color",
                ["options"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "red", "blue" }),
            });

            await Task.Delay(100);

            var pending = questionService.GetPendingQuestions();
            pending.ShouldNotBeEmpty();

            questionService.ReplyToQuestion(pending[0].Id, "blue");

            var result = await invokeTask;
            result.ShouldContain("blue");
        }
        finally
        {
            SessionContext.Clear();
        }
    }

    [Fact]
    public async Task WebSearchTool_returns_formatted_results()
    {
        var tool = ToolRegistry.Get("websearch")!;
        var result = await InvokeTool(tool, new()
        {
            ["query"] = "test query",
            ["count"] = 2,
        });

        result.ShouldContain("Fake Result");
        result.ShouldContain("https://example.com");
    }

    [Fact]
    public async Task LspTool_dispatches_to_service()
    {
        var tool = ToolRegistry.Get("lsp")!;
        var result = await InvokeTool(tool, new()
        {
            ["operation"] = "hover",
            ["filePath"] = "/some/file.cs",
            ["line"] = 1,
            ["character"] = 0,
        });

        result.ShouldContain("fake hover");
    }

    [Fact]
    public async Task SkillTool_returns_error_for_unknown_skill()
    {
        var tool = ToolRegistry.Get("skill")!;
        var result = await InvokeTool(tool, new()
        {
            ["name"] = "nonexistent-skill",
        });

        result.ToLowerInvariant().ShouldContain("not found");
    }

    // ── Fakes ──

    private sealed class FakeWebSearchProvider : IWebSearchProvider
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            string query, int count, CancellationToken cancellationToken = default)
        {
            var results = new List<WebSearchResult>
            {
                new() { Title = "Fake Result", Url = "https://example.com", Snippet = "A fake snippet" },
            };
            return Task.FromResult<IReadOnlyList<WebSearchResult>>(results);
        }
    }

    private sealed class FakeLspService : ILspService
    {
        public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<LspHoverResult?> HoverAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<LspHoverResult?>(new LspHoverResult { Content = "fake hover content" });

        public Task<IReadOnlyList<LspSymbol>> DocumentSymbolAsync(
            string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspSymbol>>([]);

        public Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(
            string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspSymbol>>([]);

        public Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspCallHierarchyItem>>([]);

        public Task<IReadOnlyList<LspCallHierarchyItem>> IncomingCallsAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspCallHierarchyItem>>([]);

        public Task<IReadOnlyList<LspCallHierarchyItem>> OutgoingCallsAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspCallHierarchyItem>>([]);

        public Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(
            string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspDiagnostic>>([]);

        public Task NotifyDocumentChangedAsync(string filePath, string newText, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LspCompletionItem>> CompletionAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspCompletionItem>>([]);

        public Task<IReadOnlyList<LspCodeAction>> CodeActionAsync(string filePath, int startLine, int startCharacter, int endLine, int endCharacter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspCodeAction>>([]);

        public Task<IReadOnlyList<LspTextEdit>> FormattingAsync(string filePath, int tabSize, bool insertSpaces, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTextEdit>>([]);

        public Task<LspWorkspaceEdit?> RenameAsync(string filePath, int line, int character, string newName, CancellationToken ct) =>
            Task.FromResult<LspWorkspaceEdit?>(null);

        public Task<LspSignatureHelp?> SignatureHelpAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<LspSignatureHelp?>(null);

        public Task<LspWorkspaceEdit?> ApplyCodeActionAsync(string filePath, int startLine, int startCharacter, int endLine, int endCharacter, int codeActionIndex, CancellationToken ct) =>
            Task.FromResult<LspWorkspaceEdit?>(null);

        public Task<LspSemanticTokens?> SemanticTokensAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<LspSemanticTokens?>(null);

        public Task<LspSemanticTokensLegend?> GetSemanticTokensLegendAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<LspSemanticTokensLegend?>(null);

        public Task<IReadOnlyList<LspInlayHint>> InlayHintAsync(string filePath, int startLine, int startCharacter, int endLine, int endCharacter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspInlayHint>>([]);

        public Task<IReadOnlyList<LspCodeLens>> CodeLensAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspCodeLens>>([]);

        public Task<IReadOnlyList<LspFoldingRange>> FoldingRangeAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspFoldingRange>>([]);

        public Task<IReadOnlyList<LspSelectionRange>> SelectionRangeAsync(string filePath, IReadOnlyList<(int Line, int Character)> positions, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspSelectionRange>>([]);

        public void OnProgress(Action<LspProgressValue> handler) { }

        public void OnServerStatusChanged(Action<LspServerStatus> handler) { }

        public Task<IReadOnlyList<LspServerStatus>> GetServerStatusAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspServerStatus>>([]);

        public Task<IReadOnlyList<LspLocation>> GoToTypeDefinitionAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspLocation>> GoToDeclarationAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspDocumentHighlight>> DocumentHighlightAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspDocumentHighlight>>([]);

        public Task<string?> ExecuteCommandAsync(
            string command, IReadOnlyList<object>? arguments, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<LspTypeHierarchyItem>> PrepareTypeHierarchyAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTypeHierarchyItem>>([]);

        public Task<IReadOnlyList<LspTypeHierarchyItem>> SupertypesAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTypeHierarchyItem>>([]);

        public Task<IReadOnlyList<LspTypeHierarchyItem>> SubtypesAsync(
            string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTypeHierarchyItem>>([]);

        public Task<IReadOnlyList<LspDocumentLink>> DocumentLinkAsync(
            string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspDocumentLink>>([]);
    }
}
