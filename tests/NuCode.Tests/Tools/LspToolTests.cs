using Microsoft.Extensions.AI;
using NuCode.Lsp;
using NuCode.Tools;

namespace NuCode;

public sealed class LspToolTests
{
    [Fact]
    public async Task ReturnsErrorWhenNoLspServiceConfigured()
    {
        var tool = new LspTool(null);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "goToDefinition",
            ["filePath"] = "/test.cs",
            ["line"] = 0,
            ["character"] = 5,
        });

        result.ShouldContain("No LSP service is configured");
    }

    [Fact]
    public async Task GoToDefinitionReturnsLocations()
    {
        var service = new FakeLspService
        {
            Definitions = [new LspLocation { FilePath = "/src/foo.cs", StartLine = 10, StartCharacter = 4, EndLine = 10, EndCharacter = 10 }],
        };
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "goToDefinition",
            ["filePath"] = "/test.cs",
            ["line"] = 5,
            ["character"] = 10,
        });

        result.ShouldContain("/src/foo.cs:11:5");
        result.ShouldContain("1 definition(s)");
    }

    [Fact]
    public async Task HoverReturnsContent()
    {
        var service = new FakeLspService
        {
            HoverContent = new LspHoverResult { Content = "```csharp\npublic class Foo\n```" },
        };
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "hover",
            ["filePath"] = "/test.cs",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("public class Foo");
    }

    [Fact]
    public async Task DocumentSymbolReturnsSymbols()
    {
        var service = new FakeLspService
        {
            Symbols =
            [
                new LspSymbol
                {
                    Name = "MyMethod",
                    Kind = "Method",
                    Location = new LspLocation { FilePath = "/test.cs", StartLine = 5, StartCharacter = 0, EndLine = 5, EndCharacter = 8 },
                    ContainerName = "MyClass",
                },
            ],
        };
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "documentSymbol",
            ["filePath"] = "/test.cs",
        });

        result.ShouldContain("[Method] MyMethod (MyClass)");
    }

    [Fact]
    public async Task WorkspaceSymbolRequiresQuery()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "workspaceSymbol",
        });

        result.ShouldContain("query is required");
    }

    [Fact]
    public async Task UnknownOperationReturnsError()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "invalidOp",
        });

        result.ShouldContain("Unknown operation");
    }

    [Fact]
    public async Task MissingFilePathReturnsError()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "goToDefinition",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("filePath is required");
    }

    private static async Task<string> Invoke(AIFunction fn, Dictionary<string, object?> args)
    {
        var result = await fn.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    private sealed class FakeLspService : ILspService
    {
        public IReadOnlyList<LspLocation> Definitions { get; set; } = [];
        public IReadOnlyList<LspLocation> References { get; set; } = [];
        public LspHoverResult? HoverContent { get; set; }
        public IReadOnlyList<LspSymbol> Symbols { get; set; } = [];
        public IReadOnlyList<LspCallHierarchyItem> CallItems { get; set; } = [];
        public bool ThrowOnDefinition { get; set; }

        public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(string filePath, int line, int character, CancellationToken ct)
        {
            if (ThrowOnDefinition) throw new ArgumentException("File path '/outside/evil.cs' is outside the workspace boundary.", nameof(filePath));
            return Task.FromResult(Definitions);
        }
        public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(References);
        public Task<LspHoverResult?> HoverAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(HoverContent);
        public Task<IReadOnlyList<LspSymbol>> DocumentSymbolAsync(string filePath, CancellationToken ct) =>
            Task.FromResult(Symbols);
        public Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(string query, CancellationToken ct) =>
            Task.FromResult(Symbols);
        public Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(Definitions);
        public Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(CallItems);
        public Task<IReadOnlyList<LspCallHierarchyItem>> IncomingCallsAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(CallItems);
        public Task<IReadOnlyList<LspCallHierarchyItem>> OutgoingCallsAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(CallItems);

        public Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct) =>
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
        public Task<IReadOnlyList<LspLocation>> GoToTypeDefinitionAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(Definitions);
        public Task<IReadOnlyList<LspLocation>> GoToDeclarationAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult(Definitions);
        public Task<IReadOnlyList<LspDocumentHighlight>> DocumentHighlightAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspDocumentHighlight>>([]);
        public Task<string?> ExecuteCommandAsync(string command, IReadOnlyList<object>? arguments, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<IReadOnlyList<LspTypeHierarchyItem>> PrepareTypeHierarchyAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTypeHierarchyItem>>([]);
        public Task<IReadOnlyList<LspTypeHierarchyItem>> SupertypesAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTypeHierarchyItem>>([]);
        public Task<IReadOnlyList<LspTypeHierarchyItem>> SubtypesAsync(string filePath, int line, int character, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspTypeHierarchyItem>>([]);
        public Task<IReadOnlyList<LspDocumentLink>> DocumentLinkAsync(string filePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LspDocumentLink>>([]);
    }

    [Fact]
    public async Task PrepareTypeHierarchyMissingFilePathReturnsError()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "prepareTypeHierarchy",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task SupertypesMissingFilePathReturnsError()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "supertypes",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task SubtypesMissingFilePathReturnsError()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "subtypes",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("filePath is required");
    }

    [Fact]
    public async Task PrepareTypeHierarchyEmptyResultFormatsCorrectly()
    {
        var service = new FakeLspService();
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "prepareTypeHierarchy",
            ["filePath"] = "/test.cs",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("No type hierarchy");
    }

    [Fact]
    public async Task Returns_friendly_error_when_file_outside_workspace()
    {
        var service = new FakeLspService
        {
            ThrowOnDefinition = true,
        };
        var tool = new LspTool(service);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "goToDefinition",
            ["filePath"] = "/outside/evil.cs",
            ["line"] = 0,
            ["character"] = 0,
        });

        result.ShouldContain("Error:");
        result.ShouldContain("outside the workspace boundary");
    }
}
