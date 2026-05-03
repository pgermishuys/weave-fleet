namespace NuCode.Lsp;

/// <summary>
/// Abstraction for Language Server Protocol operations.
/// Consumers can implement this to integrate with their own LSP client,
/// or use the opt-in built-in LSP server manager via AddNuCodeLsp().
/// </summary>
public interface ILspService
{
    /// <summary>Navigate to the definition of the symbol at the given position.</summary>
    Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Find all references to the symbol at the given position.</summary>
    Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get hover information for the symbol at the given position.</summary>
    Task<LspHoverResult?> HoverAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get all symbols in the specified document.</summary>
    Task<IReadOnlyList<LspSymbol>> DocumentSymbolAsync(
        string filePath, CancellationToken cancellationToken);

    /// <summary>Search for symbols across the workspace.</summary>
    Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(
        string query, CancellationToken cancellationToken);

    /// <summary>Navigate to the implementation of the symbol at the given position.</summary>
    Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Prepare call hierarchy at the given position.</summary>
    Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get incoming calls for a call hierarchy item.</summary>
    Task<IReadOnlyList<LspCallHierarchyItem>> IncomingCallsAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get outgoing calls for a call hierarchy item.</summary>
    Task<IReadOnlyList<LspCallHierarchyItem>> OutgoingCallsAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get cached diagnostics for the specified file.</summary>
    Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(
        string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Notify the LSP server that the document content has changed.
    /// Sends textDocument/didChange (full-content sync). Opens the document first if needed.
    /// </summary>
    Task NotifyDocumentChangedAsync(
        string filePath, string newText, CancellationToken cancellationToken);

    /// <summary>Get completion items at the given position.</summary>
    Task<IReadOnlyList<LspCompletionItem>> CompletionAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get code actions for the given range in a document.</summary>
    Task<IReadOnlyList<LspCodeAction>> CodeActionAsync(
        string filePath, int startLine, int startCharacter, int endLine, int endCharacter, CancellationToken cancellationToken);

    /// <summary>Format the entire document.</summary>
    Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string filePath, int tabSize, bool insertSpaces, CancellationToken cancellationToken);

    /// <summary>Rename the symbol at the given position across the workspace.</summary>
    Task<LspWorkspaceEdit?> RenameAsync(
        string filePath, int line, int character, string newName, CancellationToken cancellationToken);

    /// <summary>Get signature help at the given position.</summary>
    Task<LspSignatureHelp?> SignatureHelpAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve and apply the code action at the given index for the specified range.
    /// Sends codeAction/resolve then waits for the resulting workspace edit (either inline
    /// in the resolved action or delivered via a workspace/applyEdit server request).
    /// Returns null if no code action exists at that index or the server returns no edit.
    /// </summary>
    Task<LspWorkspaceEdit?> ApplyCodeActionAsync(
        string filePath, int startLine, int startCharacter, int endLine, int endCharacter,
        int codeActionIndex, CancellationToken cancellationToken);

    /// <summary>Get full semantic tokens for the specified document.</summary>
    Task<LspSemanticTokens?> SemanticTokensAsync(
        string filePath, CancellationToken cancellationToken);

    /// <summary>Get the semantic tokens legend from the server's capabilities.</summary>
    Task<LspSemanticTokensLegend?> GetSemanticTokensLegendAsync(
        string filePath, CancellationToken cancellationToken);

    /// <summary>Get inlay hints for the specified range in a document.</summary>
    Task<IReadOnlyList<LspInlayHint>> InlayHintAsync(
        string filePath, int startLine, int startCharacter, int endLine, int endCharacter,
        CancellationToken cancellationToken);

    /// <summary>Get code lenses for the specified document. Lenses are resolved automatically.</summary>
    Task<IReadOnlyList<LspCodeLens>> CodeLensAsync(
        string filePath, CancellationToken cancellationToken);

    /// <summary>Get folding ranges for the specified document.</summary>
    Task<IReadOnlyList<LspFoldingRange>> FoldingRangeAsync(
        string filePath, CancellationToken cancellationToken);

    /// <summary>Get selection ranges for the given positions in a document.</summary>
    Task<IReadOnlyList<LspSelectionRange>> SelectionRangeAsync(
        string filePath, IReadOnlyList<(int Line, int Character)> positions,
        CancellationToken cancellationToken);

    /// <summary>Register a callback to receive progress notifications from LSP servers.</summary>
    void OnProgress(Action<LspProgressValue> handler);

    /// <summary>Register a callback to receive server status change notifications.</summary>
    void OnServerStatusChanged(Action<LspServerStatus> handler);

    /// <summary>Get the current status of all managed LSP servers.</summary>
    Task<IReadOnlyList<LspServerStatus>> GetServerStatusAsync(
        CancellationToken cancellationToken);

    /// <summary>Navigate to the type definition of the symbol at the given position.</summary>
    Task<IReadOnlyList<LspLocation>> GoToTypeDefinitionAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Navigate to the declaration of the symbol at the given position.</summary>
    Task<IReadOnlyList<LspLocation>> GoToDeclarationAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get document highlights for the symbol at the given position.</summary>
    Task<IReadOnlyList<LspDocumentHighlight>> DocumentHighlightAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Execute a server-side command and return the result as a string.</summary>
    Task<string?> ExecuteCommandAsync(
        string command, IReadOnlyList<object>? arguments, CancellationToken cancellationToken);

    /// <summary>Prepare type hierarchy at the given position.</summary>
    Task<IReadOnlyList<LspTypeHierarchyItem>> PrepareTypeHierarchyAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get supertypes for the type at the given position.</summary>
    Task<IReadOnlyList<LspTypeHierarchyItem>> SupertypesAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get subtypes for the type at the given position.</summary>
    Task<IReadOnlyList<LspTypeHierarchyItem>> SubtypesAsync(
        string filePath, int line, int character, CancellationToken cancellationToken);

    /// <summary>Get document links for the specified document. Links are resolved automatically if the server supports it.</summary>
    Task<IReadOnlyList<LspDocumentLink>> DocumentLinkAsync(
        string filePath, CancellationToken cancellationToken);
}
