using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NuCode.Lsp;

namespace NuCode.Tools;

/// <summary>
/// Provides LSP code intelligence operations: goToDefinition, findReferences, hover,
/// documentSymbol, workspaceSymbol, goToImplementation, prepareCallHierarchy,
/// incomingCalls, outgoingCalls, completion, codeAction, formatting, rename, signatureHelp,
/// notifyChange, getDiagnostics, semanticTokens, inlayHints, codeLens, foldingRanges,
/// selectionRanges, serverStatus, goToTypeDefinition, goToDeclaration, documentHighlight,
/// executeCommand, prepareTypeHierarchy, supertypes, subtypes, documentLinks.
/// </summary>
internal sealed class LspTool(ILspService? lspService) : INuCodeTool
{
    public string Name => "lsp";
    public string Description => "Interact with LSP servers for code intelligence: definitions, references, hover, symbols, call hierarchy, type hierarchy, completion, code actions, formatting, rename, signature help, diagnostics, semantic tokens, inlay hints, code lens, folding ranges, selection ranges, server status, type definition, declaration, document highlight, execute command, document links.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Interact with LSP servers for code intelligence: definitions, references, hover, symbols, call hierarchy, type hierarchy, completion, code actions, formatting, rename, signature help, diagnostics, semantic tokens, inlay hints, code lens, folding ranges, selection ranges, server status, type definition, declaration, document highlight, execute command, document links.")]
    internal async Task<string> ExecuteAsync(
        [Description("The LSP operation: goToDefinition, findReferences, hover, documentSymbol, workspaceSymbol, goToImplementation, prepareCallHierarchy, incomingCalls, outgoingCalls, completion, codeAction, formatting, rename, signatureHelp, notifyChange, getDiagnostics, semanticTokens, inlayHints, codeLens, foldingRanges, selectionRanges, serverStatus, goToTypeDefinition, goToDeclaration, documentHighlight, executeCommand, prepareTypeHierarchy, supertypes, subtypes, documentLinks")]
        string operation,
        [Description("The absolute path to the file")] string? filePath = null,
        [Description("The line number (0-indexed)")] int? line = null,
        [Description("The character/column number (0-indexed)")] int? character = null,
        [Description("Query string (for workspaceSymbol)")] string? query = null,
        [Description("New text content (for notifyChange)")] string? newText = null,
        [Description("New name (for rename)")] string? newName = null,
        [Description("End line (for codeAction/inlayHints range)")] int? endLine = null,
        [Description("End character (for codeAction/inlayHints range)")] int? endCharacter = null,
        [Description("Tab size (for formatting, default 4)")] int? tabSize = null,
        [Description("Use spaces for indentation (for formatting, default true)")] bool? insertSpaces = null,
        [Description("Comma-separated positions as 'line:char,line:char' (for selectionRanges)")] string? positions = null,
        [Description("Command name (for executeCommand)")] string? command = null,
        [Description("Command arguments as JSON array (for executeCommand)")] string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (lspService is null)
        {
            return "Error: No LSP service is configured. Add an ILspService implementation or call AddNuCodeLsp() to enable LSP support.";
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            return "Error: operation is required. Valid operations: goToDefinition, findReferences, hover, documentSymbol, workspaceSymbol, goToImplementation, prepareCallHierarchy, incomingCalls, outgoingCalls, completion, codeAction, formatting, rename, signatureHelp, notifyChange, getDiagnostics, semanticTokens, inlayHints, codeLens, foldingRanges, selectionRanges, serverStatus, goToTypeDefinition, goToDeclaration, documentHighlight, executeCommand, prepareTypeHierarchy, supertypes, subtypes, documentLinks";
        }

        try
        {
            return operation.Trim().ToLowerInvariant() switch
            {
                "gotodefinition" => await GoToDefinitionAsync(filePath, line, character, cancellationToken),
                "findreferences" => await FindReferencesAsync(filePath, line, character, cancellationToken),
                "hover" => await HoverAsync(filePath, line, character, cancellationToken),
                "documentsymbol" => await DocumentSymbolAsync(filePath, cancellationToken),
                "workspacesymbol" => await WorkspaceSymbolAsync(query, cancellationToken),
                "gotoimplementation" => await GoToImplementationAsync(filePath, line, character, cancellationToken),
                "preparecallhierarchy" => await PrepareCallHierarchyAsync(filePath, line, character, cancellationToken),
                "incomingcalls" => await IncomingCallsAsync(filePath, line, character, cancellationToken),
                "outgoingcalls" => await OutgoingCallsAsync(filePath, line, character, cancellationToken),
                "completion" => await CompletionAsync(filePath, line, character, cancellationToken),
                "codeaction" => await CodeActionAsync(filePath, line, character, endLine, endCharacter, cancellationToken),
                "formatting" => await FormattingAsync(filePath, tabSize, insertSpaces, cancellationToken),
                "rename" => await RenameAsync(filePath, line, character, newName, cancellationToken),
                "signaturehelp" => await SignatureHelpAsync(filePath, line, character, cancellationToken),
                "notifychange" => await NotifyChangeAsync(filePath, newText, cancellationToken),
                "getdiagnostics" => await GetDiagnosticsAsync(filePath, cancellationToken),
                "semantictokens" => await SemanticTokensAsync(filePath, cancellationToken),
                "inlayhints" => await InlayHintsAsync(filePath, line, character, endLine, endCharacter, cancellationToken),
                "codelens" => await CodeLensAsync(filePath, cancellationToken),
                "foldingranges" => await FoldingRangesAsync(filePath, cancellationToken),
                "selectionranges" => await SelectionRangesAsync(filePath, positions, cancellationToken),
                "serverstatus" => await ServerStatusAsync(cancellationToken),
                "gototypedefinition" => await GoToTypeDefinitionAsync(filePath, line, character, cancellationToken),
                "gotodeclaration" => await GoToDeclarationAsync(filePath, line, character, cancellationToken),
                "documenthighlight" => await DocumentHighlightAsync(filePath, line, character, cancellationToken),
                "executecommand" => await ExecuteCommandAsync(command, arguments, cancellationToken),
                "preparetypehierarchy" => await PrepareTypeHierarchyAsync(filePath, line, character, cancellationToken),
                "supertypes" => await SupertypesAsync(filePath, line, character, cancellationToken),
                "subtypes" => await SubtypesAsync(filePath, line, character, cancellationToken),
                "documentlinks" => await DocumentLinksAsync(filePath, cancellationToken),
                _ => $"Error: Unknown operation '{operation}'. Valid operations: goToDefinition, findReferences, hover, documentSymbol, workspaceSymbol, goToImplementation, prepareCallHierarchy, incomingCalls, outgoingCalls, completion, codeAction, formatting, rename, signatureHelp, notifyChange, getDiagnostics, semanticTokens, inlayHints, codeLens, foldingRanges, selectionRanges, serverStatus, goToTypeDefinition, goToDeclaration, documentHighlight, executeCommand, prepareTypeHierarchy, supertypes, subtypes, documentLinks",
            };
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> GoToDefinitionAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var locations = await lspService!.GoToDefinitionAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatLocations("Definition", locations);
    }

    private async Task<string> FindReferencesAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var locations = await lspService!.FindReferencesAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatLocations("Reference", locations);
    }

    private async Task<string> HoverAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var result = await lspService!.HoverAsync(filePath!, line!.Value, character!.Value, ct);
        if (result is null)
            return "No hover information available at this position.";

        return result.Content;
    }

    private async Task<string> DocumentSymbolAsync(string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for documentSymbol.";

        var symbols = await lspService!.DocumentSymbolAsync(filePath, ct);
        return FormatSymbols(symbols);
    }

    private async Task<string> WorkspaceSymbolAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query is required for workspaceSymbol.";

        var symbols = await lspService!.WorkspaceSymbolAsync(query, ct);
        return FormatSymbols(symbols);
    }

    private async Task<string> GoToImplementationAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var locations = await lspService!.GoToImplementationAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatLocations("Implementation", locations);
    }

    private async Task<string> PrepareCallHierarchyAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.PrepareCallHierarchyAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatCallHierarchy(items);
    }

    private async Task<string> IncomingCallsAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.IncomingCallsAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatCallHierarchy(items, "Incoming");
    }

    private async Task<string> OutgoingCallsAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.OutgoingCallsAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatCallHierarchy(items, "Outgoing");
    }

    private async Task<string> CompletionAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.CompletionAsync(filePath!, line!.Value, character!.Value, ct);
        if (items.Count == 0)
            return "No completions available at this position.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {items.Count} completion(s):");
        foreach (var item in items)
        {
            var detail = item.Detail is not null ? $" - {item.Detail}" : "";
            var insert = item.InsertText is not null && item.InsertText != item.Label ? $" (insert: {item.InsertText})" : "";
            sb.AppendLine($"  [{item.Kind}] {item.Label}{detail}{insert}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> CodeActionAsync(string? filePath, int? line, int? character, int? endLine, int? endCharacter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required.";
        if (line is null)
            return "Error: line is required for codeAction (start line of range).";
        if (character is null)
            return "Error: character is required for codeAction (start character of range).";

        var el = endLine ?? line.Value;
        var ec = endCharacter ?? character.Value;

        var actions = await lspService!.CodeActionAsync(filePath, line.Value, character.Value, el, ec, ct);
        if (actions.Count == 0)
            return "No code actions available for this range.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {actions.Count} code action(s):");
        foreach (var action in actions)
        {
            var kind = action.Kind is not null ? $" [{action.Kind}]" : "";
            var preferred = action.IsPreferred ? " (preferred)" : "";
            sb.AppendLine($"  {action.Title}{kind}{preferred}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> FormattingAsync(string? filePath, int? tabSize, bool? insertSpaces, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for formatting.";

        var edits = await lspService!.FormattingAsync(filePath, tabSize ?? 4, insertSpaces ?? true, ct);
        if (edits.Count == 0)
            return "No formatting changes needed.";

        var sb = new StringBuilder();
        sb.AppendLine($"Formatting produced {edits.Count} edit(s):");
        foreach (var edit in edits)
        {
            sb.AppendLine($"  [{edit.StartLine}:{edit.StartCharacter}-{edit.EndLine}:{edit.EndCharacter}] → \"{Truncate(edit.NewText, 60)}\"");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> RenameAsync(string? filePath, int? line, int? character, string? newName, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;
        if (string.IsNullOrWhiteSpace(newName))
            return "Error: newName is required for rename.";

        var edit = await lspService!.RenameAsync(filePath!, line!.Value, character!.Value, newName, ct);
        if (edit is null)
            return "Rename not supported or no changes produced.";

        var totalEdits = edit.Changes.Values.Sum(e => e.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"Rename produced {totalEdits} edit(s) across {edit.Changes.Count} file(s):");
        foreach (var (file, edits) in edit.Changes)
        {
            sb.AppendLine($"  {file}: {edits.Count} edit(s)");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> SignatureHelpAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var help = await lspService!.SignatureHelpAsync(filePath!, line!.Value, character!.Value, ct);
        if (help is null || help.Signatures.Count == 0)
            return "No signature help available at this position.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {help.Signatures.Count} signature(s):");
        foreach (var sig in help.Signatures)
        {
            sb.AppendLine($"  {sig.Label}");
            if (sig.Documentation is not null)
                sb.AppendLine($"    {sig.Documentation}");
            if (sig.Parameters is { Count: > 0 })
            {
                foreach (var p in sig.Parameters)
                {
                    var pDoc = p.Documentation is not null ? $" - {p.Documentation}" : "";
                    sb.AppendLine($"    param: {p.Label}{pDoc}");
                }
            }
        }
        if (help.ActiveSignature is not null)
            sb.AppendLine($"  Active signature: {help.ActiveSignature}, Active parameter: {help.ActiveParameter}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> NotifyChangeAsync(string? filePath, string? newText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for notifyChange.";
        if (newText is null)
            return "Error: newText is required for notifyChange.";

        await lspService!.NotifyDocumentChangedAsync(filePath, newText, ct);
        return "Document change notification sent.";
    }

    private async Task<string> GetDiagnosticsAsync(string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for getDiagnostics.";

        var diagnostics = await lspService!.GetDiagnosticsAsync(filePath, ct);
        if (diagnostics.Count == 0)
            return "No diagnostics found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {diagnostics.Count} diagnostic(s):");
        foreach (var d in diagnostics)
        {
            var code = d.Code is not null ? $" ({d.Code})" : "";
            var source = d.Source is not null ? $" [{d.Source}]" : "";
            sb.AppendLine($"  [{d.Severity}] {d.StartLine + 1}:{d.StartCharacter + 1} {d.Message}{code}{source}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> SemanticTokensAsync(string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for semanticTokens.";

        var tokens = await lspService!.SemanticTokensAsync(filePath, ct);
        if (tokens is null || tokens.Data.Count == 0)
            return "No semantic tokens available.";

        var legend = await lspService!.GetSemanticTokensLegendAsync(filePath, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Semantic tokens ({tokens.Data.Count / 5} token(s), {tokens.Data.Count} integers):");
        // Decode relative tokens: each group of 5 ints = deltaLine, deltaStartChar, length, tokenType, tokenModifiers
        var currentLine = 0;
        var currentChar = 0;
        for (var i = 0; i + 4 < tokens.Data.Count; i += 5)
        {
            var deltaLine = tokens.Data[i];
            var deltaChar = tokens.Data[i + 1];
            var length = tokens.Data[i + 2];
            var tokenTypeIndex = tokens.Data[i + 3];
            var tokenModifiers = tokens.Data[i + 4];

            currentLine += deltaLine;
            currentChar = deltaLine > 0 ? deltaChar : currentChar + deltaChar;

            var typeName = legend is not null && tokenTypeIndex < legend.TokenTypes.Count
                ? legend.TokenTypes[tokenTypeIndex]
                : $"type#{tokenTypeIndex}";

            sb.AppendLine($"  {currentLine + 1}:{currentChar + 1} len={length} {typeName} modifiers=0x{tokenModifiers:X}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> InlayHintsAsync(string? filePath, int? line, int? character, int? endLine, int? endCharacter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for inlayHints.";
        if (line is null)
            return "Error: line is required for inlayHints (start line of range).";
        if (character is null)
            return "Error: character is required for inlayHints (start character of range).";

        var el = endLine ?? line.Value;
        var ec = endCharacter ?? character.Value;

        var hints = await lspService!.InlayHintAsync(filePath, line.Value, character.Value, el, ec, ct);
        if (hints.Count == 0)
            return "No inlay hints found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {hints.Count} inlay hint(s):");
        foreach (var h in hints)
        {
            var kind = h.Kind is not null ? $" [{h.Kind}]" : "";
            sb.AppendLine($"  {h.Line + 1}:{h.Character + 1}{kind} {h.Label}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> CodeLensAsync(string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for codeLens.";

        var lenses = await lspService!.CodeLensAsync(filePath, ct);
        if (lenses.Count == 0)
            return "No code lenses found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {lenses.Count} code lens(es):");
        foreach (var lens in lenses)
        {
            var cmd = lens.CommandTitle is not null ? $" {lens.CommandTitle}" : " (unresolved)";
            sb.AppendLine($"  [{lens.StartLine + 1}:{lens.StartCharacter + 1}-{lens.EndLine + 1}:{lens.EndCharacter + 1}]{cmd}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> FoldingRangesAsync(string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for foldingRanges.";

        var ranges = await lspService!.FoldingRangeAsync(filePath, ct);
        if (ranges.Count == 0)
            return "No folding ranges found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {ranges.Count} folding range(s):");
        foreach (var r in ranges)
        {
            var kind = r.Kind is not null ? $" [{r.Kind}]" : "";
            sb.AppendLine($"  {r.StartLine + 1}-{r.EndLine + 1}{kind}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> SelectionRangesAsync(string? filePath, string? positions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for selectionRanges.";
        if (string.IsNullOrWhiteSpace(positions))
            return "Error: positions is required for selectionRanges (comma-separated 'line:char' pairs).";

        var parsed = ParsePositions(positions);
        if (parsed.Count == 0)
            return "Error: could not parse positions. Use format 'line:char,line:char'.";

        var ranges = await lspService!.SelectionRangeAsync(filePath, parsed, ct);
        if (ranges.Count == 0)
            return "No selection ranges found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {ranges.Count} selection range(s):");
        foreach (var r in ranges)
        {
            var depth = 0;
            var current = r;
            sb.Append($"  [{r.StartLine + 1}:{r.StartCharacter + 1}-{r.EndLine + 1}:{r.EndCharacter + 1}]");
            current = current.Parent;
            while (current is not null)
            {
                depth++;
                current = current.Parent;
            }
            sb.AppendLine($" (depth={depth})");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ServerStatusAsync(CancellationToken ct)
    {
        var statuses = await lspService!.GetServerStatusAsync(ct);
        if (statuses.Count == 0)
            return "No LSP servers configured.";

        var sb = new StringBuilder();
        sb.AppendLine($"LSP server status ({statuses.Count}):");
        foreach (var s in statuses)
        {
            var state = s.IsFaulted ? "FAULTED" : s.IsRunning ? "running" : "stopped";
            sb.AppendLine($"  {s.ServerName}: {state} (restarts: {s.RestartCount}/{s.MaxRestarts})");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> GoToTypeDefinitionAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var locations = await lspService!.GoToTypeDefinitionAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatLocations("Type definition", locations);
    }

    private async Task<string> GoToDeclarationAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var locations = await lspService!.GoToDeclarationAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatLocations("Declaration", locations);
    }

    private async Task<string> DocumentHighlightAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var highlights = await lspService!.DocumentHighlightAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatDocumentHighlights(highlights);
    }

    private async Task<string> PrepareTypeHierarchyAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.PrepareTypeHierarchyAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatTypeHierarchy(items);
    }

    private async Task<string> SupertypesAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.SupertypesAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatTypeHierarchy(items, "Supertype");
    }

    private async Task<string> SubtypesAsync(string? filePath, int? line, int? character, CancellationToken ct)
    {
        if (!ValidatePositionArgs(filePath, line, character, out var error))
            return error;

        var items = await lspService!.SubtypesAsync(filePath!, line!.Value, character!.Value, ct);
        return FormatTypeHierarchy(items, "Subtype");
    }

    private async Task<string> ExecuteCommandAsync(string? command, string? arguments, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Error: command is required for executeCommand.";

        IReadOnlyList<object>? args = null;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<object>>(arguments);
                args = parsed;
            }
            catch (JsonException)
            {
                return "Error: arguments must be a valid JSON array.";
            }
        }

        var result = await lspService!.ExecuteCommandAsync(command, args, ct);
        return result is not null ? $"Command result: {result}" : "Command executed (no result).";
    }

    private static IReadOnlyList<(int Line, int Character)> ParsePositions(string positions)
    {
        var result = new List<(int, int)>();
        foreach (var pair in positions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var l) && int.TryParse(parts[1], out var c))
            {
                result.Add((l, c));
            }
        }
        return result;
    }

    private static bool ValidatePositionArgs(string? filePath, int? line, int? character, out string error)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "Error: filePath is required.";
            return false;
        }
        if (line is null)
        {
            error = "Error: line is required.";
            return false;
        }
        if (character is null)
        {
            error = "Error: character is required.";
            return false;
        }
        error = "";
        return true;
    }

    private static string FormatLocations(string label, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
            return $"No {label.ToLowerInvariant()}s found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {locations.Count} {label.ToLowerInvariant()}(s):");
        foreach (var loc in locations)
        {
            sb.AppendLine($"  {loc}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatSymbols(IReadOnlyList<LspSymbol> symbols)
    {
        if (symbols.Count == 0)
            return "No symbols found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {symbols.Count} symbol(s):");
        foreach (var sym in symbols)
        {
            var container = sym.ContainerName is not null ? $" ({sym.ContainerName})" : "";
            sb.AppendLine($"  [{sym.Kind}] {sym.Name}{container} at {sym.Location}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatCallHierarchy(IReadOnlyList<LspCallHierarchyItem> items, string? direction = null)
    {
        if (items.Count == 0)
        {
            var msg = direction is not null ? $"No {direction.ToLowerInvariant()} calls found." : "No call hierarchy items found.";
            return msg;
        }

        var sb = new StringBuilder();
        var label = direction is not null ? $"{direction} call" : "Call hierarchy item";
        sb.AppendLine($"Found {items.Count} {label.ToLowerInvariant()}(s):");
        foreach (var item in items)
        {
            var detail = item.Detail is not null ? $" - {item.Detail}" : "";
            sb.AppendLine($"  [{item.Kind}] {item.Name}{detail} at {item.Location}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatTypeHierarchy(IReadOnlyList<LspTypeHierarchyItem> items, string? label = null)
    {
        if (items.Count == 0)
        {
            var msg = label is not null ? $"No {label.ToLowerInvariant()}s found." : "No type hierarchy items found.";
            return msg;
        }

        var sb = new StringBuilder();
        var itemLabel = label is not null ? label.ToLowerInvariant() : "Type hierarchy item";
        sb.AppendLine($"Found {items.Count} {itemLabel}(s):");
        foreach (var item in items)
        {
            var detail = item.Detail is not null ? $" - {item.Detail}" : "";
            sb.AppendLine($"  [{item.Kind}] {item.Name}{detail} at {item.Location}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatDocumentHighlights(IReadOnlyList<LspDocumentHighlight> highlights)
    {
        if (highlights.Count == 0)
            return "No document highlights found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {highlights.Count} highlight(s):");
        foreach (var h in highlights)
        {
            sb.AppendLine($"  [{h.Kind}] {h.StartLine + 1}:{h.StartCharacter + 1}-{h.EndLine + 1}:{h.EndCharacter + 1}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> DocumentLinksAsync(string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath is required for documentLinks.";

        var links = await lspService!.DocumentLinkAsync(filePath, ct);
        return FormatDocumentLinks(links);
    }

    private static string FormatDocumentLinks(IReadOnlyList<LspDocumentLink> links)
    {
        if (links.Count == 0)
            return "No document links found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {links.Count} document link(s):");
        foreach (var link in links)
        {
            var tooltip = link.Tooltip is not null ? $" ({link.Tooltip})" : "";
            sb.AppendLine($"  [L{link.StartLine + 1}-L{link.EndLine + 1}] {link.Target ?? "(unresolved)"}{tooltip}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");
}
