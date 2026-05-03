namespace NuCode.Lsp;

/// <summary>
/// Thrown when an LSP server sends a request for a method that is not supported by the client.
/// Maps to JSON-RPC error code -32601 (Method not found).
/// </summary>
internal sealed class LspMethodNotFoundException : Exception
{
    public LspMethodNotFoundException(string method)
        : base($"Method not found: {method}")
    {
    }
}
