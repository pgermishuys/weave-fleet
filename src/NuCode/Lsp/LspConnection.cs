using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NuCode.Lsp;

/// <summary>
/// Manages a single LSP server connection via stdio transport.
/// Handles JSON-RPC request/response correlation and message framing.
/// </summary>
internal sealed class LspConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _readTask;
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Dynamic registration: maps registration id → method name
    private readonly ConcurrentDictionary<string, string> _dynamicRegistrations = new(StringComparer.Ordinal);
    // Fast lookup: methods that are dynamically registered
    private readonly ConcurrentDictionary<string, bool> _dynamicallyRegisteredMethods = new(StringComparer.Ordinal);

    // Methods for which the client advertised dynamicRegistration = true in capabilities
    private static readonly HashSet<string> AllowedDynamicRegistrationMethods = new(StringComparer.Ordinal)
    {
        "textDocument/documentLink",
        "workspace/didChangeWatchedFiles",
    };

    /// <summary>Server capabilities returned in the initialize response.</summary>
    public System.Text.Json.JsonElement? ServerCapabilities { get; private set; }

    // Maps LSP request method names to the capability property that advertises support.
    private static readonly Dictionary<string, string> MethodToCapability = new(StringComparer.Ordinal)
    {
        ["textDocument/hover"]              = "hoverProvider",
        ["textDocument/definition"]         = "definitionProvider",
        ["textDocument/references"]         = "referencesProvider",
        ["textDocument/implementation"]     = "implementationProvider",
        ["textDocument/typeDefinition"]     = "typeDefinitionProvider",
        ["textDocument/declaration"]        = "declarationProvider",
        ["textDocument/documentSymbol"]     = "documentSymbolProvider",
        ["textDocument/completion"]         = "completionProvider",
        ["textDocument/signatureHelp"]      = "signatureHelpProvider",
        ["textDocument/codeAction"]         = "codeActionProvider",
        ["textDocument/formatting"]         = "documentFormattingProvider",
        ["textDocument/rangeFormatting"]    = "documentRangeFormattingProvider",
        ["textDocument/rename"]             = "renameProvider",
        ["textDocument/prepareCallHierarchy"] = "callHierarchyProvider",
        ["callHierarchy/incomingCalls"]     = "callHierarchyProvider",
        ["callHierarchy/outgoingCalls"]     = "callHierarchyProvider",
        ["textDocument/prepareTypeHierarchy"] = "typeHierarchyProvider",
        ["typeHierarchy/supertypes"]        = "typeHierarchyProvider",
        ["typeHierarchy/subtypes"]          = "typeHierarchyProvider",
        ["workspace/symbol"]                = "workspaceSymbolProvider",
        ["textDocument/diagnostic"]         = "diagnosticProvider",
        ["codeAction/resolve"]              = "codeActionProvider",
        ["textDocument/semanticTokens/full"] = "semanticTokensProvider",
        ["textDocument/inlayHint"]           = "inlayHintProvider",
        ["textDocument/codeLens"]            = "codeLensProvider",
        ["codeLens/resolve"]                 = "codeLensProvider",
        ["textDocument/foldingRange"]        = "foldingRangeProvider",
        ["textDocument/selectionRange"]      = "selectionRangeProvider",
        ["textDocument/documentHighlight"]   = "documentHighlightProvider",
        ["textDocument/documentLink"]        = "documentLinkProvider",
        ["documentLink/resolve"]             = "documentLinkProvider",
        ["workspace/executeCommand"]         = "executeCommandProvider",
    };

    /// <summary>
    /// Returns true if the server advertised support for the given LSP method.
    /// Checks dynamic registrations first, then falls back to static capabilities.
    /// </summary>
    public bool SupportsCapability(string method)
    {
        if (_dynamicallyRegisteredMethods.TryGetValue(method, out _)) return true;
        return CheckCapability(ServerCapabilities, method);
    }

    /// <summary>
    /// Registers a dynamically provided capability.
    /// </summary>
    internal void RegisterCapability(string id, string method)
    {
        if (!AllowedDynamicRegistrationMethods.Contains(method)) return;
        _dynamicRegistrations[id] = method;
        _dynamicallyRegisteredMethods[method] = true;
    }

    /// <summary>
    /// Unregisters a dynamically provided capability.
    /// </summary>
    internal void UnregisterCapability(string id)
    {
        if (_dynamicRegistrations.TryRemove(id, out var method))
        {
            // Only remove from fast lookup if no other registration provides this method
            var stillRegistered = false;
            foreach (var kvp in _dynamicRegistrations)
            {
                if (kvp.Value == method) { stillRegistered = true; break; }
            }
            if (!stillRegistered) _dynamicallyRegisteredMethods.TryRemove(method, out _);
        }
    }

    /// <summary>
    /// Static capability check — inspects a ServerCapabilities JSON element to determine
    /// whether the given LSP method is supported. Exposed internal for unit testing.
    /// </summary>
    internal static bool CheckCapability(JsonElement? serverCapabilities, string method)
    {
        if (serverCapabilities is null) return true; // optimistic: not yet known
        if (!MethodToCapability.TryGetValue(method, out var capName)) return true; // no mapping → allow

        if (!serverCapabilities.Value.TryGetProperty(capName, out var capValue)) return false;

        return capValue.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            JsonValueKind.Object => true, // capability object = supported
            JsonValueKind.Number => capValue.GetInt32() != 0,
            _ => true,
        };
    }

    /// <summary>True once the connection has been initialized and is ready to handle requests.</summary>
    public bool IsReady => _readyTcs.Task.IsCompleted;

    /// <summary>True if the server process exited unexpectedly.</summary>
    public bool IsFaulted => Volatile.Read(ref _isFaulted);
    private bool _isFaulted;

    /// <summary>Waits until the connection is ready, or until <paramref name="ct"/> is cancelled.</summary>
    public Task WaitForReadyAsync(CancellationToken ct = default)
    {
        if (IsReady) return Task.CompletedTask;
        return _readyTcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Called when the server sends a notification (has method, no id).
    /// Parameters: method, params.
    /// </summary>
    public Action<string, JsonElement?>? OnNotification { get; set; }

    /// <summary>
    /// Called when the server sends a request (has both method and id).
    /// Parameters: method, id, params. Must return a result to send back.
    /// </summary>
    public Func<string, int, JsonElement?, Task<object?>>? OnServerRequest { get; set; }

    private LspConnection(Process process)
    {
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        _readTask = Task.Run(ReadLoopAsync);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Volatile.Write(ref _isFaulted, true);
        // Complete all pending requests with null (error)
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetResult(null);
        }
        _readyTcs.TrySetResult(); // unblock any waiters
    }

    public static async Task<LspConnection?> StartAsync(
        IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, object?>? initializationOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (command.Count == 0) return null;

        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        for (var i = 1; i < command.Count; i++)
        {
            psi.ArgumentList.Add(command[i]);
        }

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start LSP server process.");
        }
        catch (Exception)
        {
            return null;
        }

        var connection = new LspConnection(process);

        // Build client capabilities so servers enable their features
        var capabilities = new
        {
            textDocument = new
            {
                synchronization = new
                {
                    dynamicRegistration = false,
                    didSave = true,
                    willSave = false,
                    change = 1, // TextDocumentSyncKind.Full
                },
                hover = new { dynamicRegistration = false },
                definition = new { dynamicRegistration = false },
                references = new { dynamicRegistration = false },
                implementation = new { dynamicRegistration = false, linkSupport = false },
                documentSymbol = new { dynamicRegistration = false, hierarchicalDocumentSymbolSupport = true },
                callHierarchy = new { dynamicRegistration = false },
                typeHierarchy = new { dynamicRegistration = false },
                publishDiagnostics = new { relatedInformation = true, versionSupport = false },
                declaration = new { dynamicRegistration = false },
                typeDefinition = new { dynamicRegistration = false },
                completion = new
                {
                    dynamicRegistration = false,
                    completionItem = new { snippetSupport = false },
                },
                codeAction = new
                {
                    dynamicRegistration = false,
                    codeActionLiteralSupport = new
                    {
                        codeActionKind = new
                        {
                            valueSet = new[] { "quickfix", "refactor", "refactor.extract", "refactor.inline", "source" }
                        }
                    },
                    isPreferredSupport = true,
                    resolveSupport = new { properties = new[] { "edit" } },
                },
                formatting = new { dynamicRegistration = false },
                rename = new { dynamicRegistration = false, prepareSupport = false },
                signatureHelp = new
                {
                    dynamicRegistration = false,
                    signatureInformation = new
                    {
                        parameterInformation = new { labelOffsetSupport = false },
                    },
                },
                semanticTokens = new
                {
                    dynamicRegistration = false,
                    requests = new { full = true },
                    tokenTypes = Array.Empty<string>(),
                    tokenModifiers = Array.Empty<string>(),
                    formats = new[] { "relative" },
                    multilineTokenSupport = false,
                    overlappingTokenSupport = false,
                },
                inlayHint = new { dynamicRegistration = false },
                codeLens = new { dynamicRegistration = false },
                foldingRange = new { dynamicRegistration = false },
                selectionRange = new { dynamicRegistration = false },
                documentHighlight = new { dynamicRegistration = false },
                documentLink = new { dynamicRegistration = true },
            },
            workspace = new
            {
                symbol = new { dynamicRegistration = false },
                workspaceFolders = false,
                configuration = false,
                didChangeWatchedFiles = new { dynamicRegistration = true },
            },
        };

        var initResult = await connection.SendRequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            capabilities,
            rootUri = workingDirectory is not null ? new Uri(workingDirectory).AbsoluteUri : (string?)null,
            initializationOptions = (object?)initializationOptions,
        }, cancellationToken);

        if (initResult is null)
        {
            await connection.DisposeAsync();
            return null;
        }

        // Store server capabilities for later use
        if (initResult.Value.TryGetProperty("capabilities", out var serverCaps))
        {
            connection.ServerCapabilities = serverCaps;
        }

        await connection.SendNotificationAsync("initialized", new { });
        connection._readyTcs.TrySetResult();
        return connection;
    }

    public async Task<JsonElement?> SendRequestAsync(string method, object? @params, CancellationToken ct = default)
    {
        var id = LspProtocol.NextId();
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var json = LspProtocol.CreateRequest(id, method, @params);
        var bytes = LspProtocol.EncodeMessage(json);

        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(bytes, ct);
                await _process.StandardInput.BaseStream.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task SendNotificationAsync(string method, object? @params)
    {
        var json = LspProtocol.CreateNotification(method, @params);
        var bytes = LspProtocol.EncodeMessage(json);
        await _writeLock.WaitAsync();
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(bytes);
            await _process.StandardInput.BaseStream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendResponseAsync(int id, object? result)
    {
        var json = LspProtocol.CreateResponse(id, result);
        var bytes = LspProtocol.EncodeMessage(json);
        await _writeLock.WaitAsync();
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(bytes);
            await _process.StandardInput.BaseStream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendErrorResponseAsync(int id, int code, string message)
    {
        var json = LspProtocol.CreateErrorResponse(id, code, message);
        var bytes = LspProtocol.EncodeMessage(json);
        await _writeLock.WaitAsync();
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(bytes);
            await _process.StandardInput.BaseStream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _process.StandardOutput.BaseStream;
        var buffer = new byte[4096];
        var leftover = Array.Empty<byte>();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0) break;

                var data = new byte[leftover.Length + read];
                leftover.CopyTo(data, 0);
                buffer.AsSpan(0, read).CopyTo(data.AsSpan(leftover.Length));
                leftover = data;

                // Parse messages from buffer
                while (TryExtractMessage(ref leftover, out var messageJson))
                {
                    _ = Task.Run(() => HandleMessageAsync(messageJson));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose
        }
        catch
        {
            // Connection broken
        }
    }

    private static bool TryExtractMessage(ref byte[] data, out string message)
    {
        message = "";
        var text = Encoding.UTF8.GetString(data);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return false;

        var header = text[..headerEnd];
        var contentLengthPrefix = "Content-Length: ";
        var clIndex = header.IndexOf(contentLengthPrefix, StringComparison.OrdinalIgnoreCase);
        if (clIndex < 0) return false;

        var clValueStart = clIndex + contentLengthPrefix.Length;
        var clValueEnd = header.IndexOf('\r', clValueStart);
        if (clValueEnd < 0) clValueEnd = header.Length;

        if (!int.TryParse(header[clValueStart..clValueEnd], out var contentLength))
            return false;

        var bodyStart = headerEnd + 4;
        var bodyBytes = Encoding.UTF8.GetByteCount(text[..bodyStart]);
        var totalNeeded = bodyBytes + contentLength;

        if (data.Length < totalNeeded) return false;

        message = Encoding.UTF8.GetString(data, bodyBytes, contentLength);
        data = data[totalNeeded..];
        return true;
    }

    private async Task HandleMessageAsync(string json)
    {
        try
        {
            var msg = LspProtocol.ParseIncoming(json);
            if (msg is null) return;

            if (msg.IsResponse)
            {
                // Response to one of our requests
                if (msg.Id is not null && _pending.TryGetValue(msg.Id.Value, out var tcs))
                {
                    tcs.TrySetResult(msg.Error is not null ? null : msg.Result);
                }
            }
            else if (msg.IsNotification)
            {
                // Server-initiated notification
                OnNotification?.Invoke(msg.Method!, msg.Params);
            }
            else if (msg.IsServerRequest)
            {
                // Server-initiated request — must respond
                var reqId = msg.Id!.Value;
                var handler = OnServerRequest;
                if (handler is not null)
                {
                    try
                    {
                        var result = await handler(msg.Method!, reqId, msg.Params);
                        await SendResponseAsync(reqId, result);
                    }
                    catch (LspMethodNotFoundException ex)
                    {
                        await SendErrorResponseAsync(reqId, -32601, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        await SendErrorResponseAsync(reqId, -32603, ex.Message);
                    }
                }
                else
                {
                    // Method not found
                    await SendErrorResponseAsync(reqId, -32601, $"Method not found: {msg.Method}");
                }
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    public async ValueTask DisposeAsync()
    {
        // LSP spec: send shutdown REQUEST (await response), then exit NOTIFICATION, then kill.
        // Must NOT cancel CTS before sending — that would abort the write and the read loop.
        try
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendRequestAsync("shutdown", null, shutdownCts.Token);
            await SendNotificationAsync("exit", null);
        }
        catch
        {
            // Best effort — server may already be dead
        }

        await _cts.CancelAsync();

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already exited
        }

        _process.Dispose();
        _cts.Dispose();

        try { await _readTask; } catch { /* ignore */ }
    }
}
