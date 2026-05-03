using System.Collections.Concurrent;

namespace NuCode.Lsp;

/// <summary>
/// Tracks which document URIs are open for a single LSP connection.
/// Handles didOpen/didClose lifecycle and version incrementing.
/// </summary>
internal sealed class OpenDocumentTracker
{
    private readonly LspConnection _connection;
    private readonly ConcurrentDictionary<string, int> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _openLock = new(1, 1);

    public OpenDocumentTracker(LspConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Ensures the document at <paramref name="filePath"/> has been sent to the server via didOpen.
    /// If already open, this is a no-op.
    /// </summary>
    public async Task EnsureOpenAsync(string filePath, CancellationToken ct = default)
    {
        var uri = FileToUri(filePath);

        if (_openDocuments.ContainsKey(uri)) return;

        await _openLock.WaitAsync(ct);
        try
        {
            if (_openDocuments.ContainsKey(uri)) return;

            string text;
            try
            {
                text = await File.ReadAllTextAsync(filePath, ct);
            }
            catch
            {
                text = "";
            }

            var languageId = LspLanguageId.GetLanguageId(Path.GetExtension(filePath));

            await _connection.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri,
                    languageId,
                    version = 1,
                    text,
                },
            });

            _openDocuments[uri] = 1;
        }
        finally
        {
            _openLock.Release();
        }
    }

    /// <summary>
    /// Sends didClose for the document and removes it from the open-document set.
    /// </summary>
    public async Task CloseAsync(string filePath)
    {
        var uri = FileToUri(filePath);
        if (!_openDocuments.TryRemove(uri, out _)) return;

        await _connection.SendNotificationAsync("textDocument/didClose", new
        {
            textDocument = new { uri },
        });
    }

    /// <summary>
    /// Closes all currently open documents. Called during dispose.
    /// </summary>
    public async Task CloseAllAsync()
    {
        var uris = _openDocuments.Keys.ToList();
        _openDocuments.Clear();

        foreach (var uri in uris)
        {
            try
            {
                await _connection.SendNotificationAsync("textDocument/didClose", new
                {
                    textDocument = new { uri },
                });
            }
            catch
            {
                // Best effort
            }
        }
    }

    /// <summary>
    /// Sends a didChange notification for the document with full-content sync (kind=Full).
    /// If the document is not yet open, opens it first.
    /// </summary>
    public async Task SendChangeAsync(string filePath, string newText, CancellationToken ct = default)
    {
        var uri = FileToUri(filePath);

        await _openLock.WaitAsync(ct);
        try
        {
            if (!_openDocuments.ContainsKey(uri))
            {
                // Not open yet — open it now
                var languageId = LspLanguageId.GetLanguageId(Path.GetExtension(filePath));
                await _connection.SendNotificationAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri,
                        languageId,
                        version = 1,
                        text = newText,
                    },
                });
                _openDocuments[uri] = 1;
                return;
            }

            var nextVersion = _openDocuments[uri] + 1;
            _openDocuments[uri] = nextVersion;

            await _connection.SendNotificationAsync("textDocument/didChange", new
            {
                textDocument = new { uri, version = nextVersion },
                contentChanges = new[]
                {
                    new { text = newText },
                },
            });
        }
        finally
        {
            _openLock.Release();
        }
    }

    /// <summary>Returns the current version for a tracked document, or null if not open.</summary>
    public int? GetVersion(string filePath)
    {
        var uri = FileToUri(filePath);
        return _openDocuments.TryGetValue(uri, out var v) ? v : null;
    }

    private static string FileToUri(string filePath) => new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

    public void Dispose() => _openLock.Dispose();
}
