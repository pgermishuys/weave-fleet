using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NuCode.Configuration;

namespace NuCode.Lsp;

/// <summary>
/// Built-in LSP server manager that manages LSP server processes based on configuration.
/// Routes requests to the appropriate server by file extension.
/// Register via AddNuCodeLsp() — this is opt-in.
/// </summary>
internal sealed class LspServerManager : ILspService, IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly IOptionsMonitor<NuCodeConfig> _configMonitor;
    private readonly ILogger<LspServerManager> _logger;
    private readonly ConcurrentDictionary<string, LspConnection> _connections = new();
    private readonly ConcurrentDictionary<string, OpenDocumentTracker> _trackers = new();
    private readonly ConcurrentDictionary<string, int> _restartCounts = new();
    private const int MaxRestarts = 3;
    private readonly ConcurrentDictionary<string, IReadOnlyList<LspDiagnostic>> _diagnosticsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<string>> _extensionToServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LspServerConfig> _effectiveConfig = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    // Stores last pull-diagnostics resultId per "serverName|filePath" key for subsequent pull requests.
    private readonly ConcurrentDictionary<string, string> _diagnosticsResultId = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LspWorkspaceEdit> _pendingEdits = new();
    private readonly FileWatcherService _fileWatcher;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverLocks = new(StringComparer.OrdinalIgnoreCase);
    private event Action<LspProgressValue>? _onProgress;
    private event Action<LspServerStatus>? _onServerStatusChanged;
    private LspSemanticTokensLegend? _cachedLegend;

    public LspServerManager(string workingDirectory, IOptionsMonitor<NuCodeConfig> configMonitor)
        : this(workingDirectory, configMonitor, NullLogger<LspServerManager>.Instance)
    {
    }

    public LspServerManager(string workingDirectory, IOptionsMonitor<NuCodeConfig> configMonitor, ILogger<LspServerManager> logger)
    {
        _workingDirectory = workingDirectory;
        _configMonitor = configMonitor;
        _logger = logger;
        _fileWatcher = new FileWatcherService(workingDirectory);
        _fileWatcher.OnChanges += OnFileChanges;
        BuildExtensionMap();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var config = _configMonitor.CurrentValue;
            var autoDetect = config.LspAutoDetect ?? true;

            if (autoDetect)
            {
                _logger.LogDebug("Auto-detecting LSP servers on PATH...");
                var detected = await LspServerPresets.DetectAvailableAsync(ct);
                var merged = LspServerPresets.MergeWithConfig(detected, config.Lsp);

                foreach (var (name, serverConfig) in merged)
                {
                    _effectiveConfig[name] = serverConfig;
                }

                _logger.LogInformation("Detected {Count} LSP server(s) on PATH", detected.Count);
            }
            else if (config.Lsp is not null)
            {
                foreach (var (name, serverConfig) in config.Lsp)
                {
                    _effectiveConfig[name] = serverConfig;
                }
            }

            // Rebuild extension map with effective config
            _extensionToServer.Clear();
            foreach (var (name, serverConfig) in _effectiveConfig)
            {
                if (serverConfig.Disabled == true || serverConfig.Extensions is null) continue;
                foreach (var ext in serverConfig.Extensions)
                {
                    _extensionToServer.AddOrUpdate(ext,
                        _ => [name],
                        (_, existing) => { existing.Add(name); return existing; });
                }
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void BuildExtensionMap()
    {
        var lspConfig = _configMonitor.CurrentValue.Lsp;
        if (lspConfig is null) return;

        foreach (var (name, config) in lspConfig)
        {
            if (config.Disabled == true || config.Extensions is null) continue;
            foreach (var ext in config.Extensions)
            {
                _extensionToServer.AddOrUpdate(ext,
                    _ => [name],
                    (_, existing) => { existing.Add(name); return existing; });
            }
        }
    }

    private void ValidateFilePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var workspaceRoot = Path.GetFullPath(_workingDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, Path.GetFullPath(_workingDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"File path '{filePath}' is outside the workspace boundary.", nameof(filePath));
        }
    }

    private async Task<IReadOnlyList<(LspConnection Connection, OpenDocumentTracker Tracker)>> GetOrStartConnectionsAsync(string filePath, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext) || !_extensionToServer.TryGetValue(ext, out var serverNames))
        {
            return [];
        }

        var results = new List<(LspConnection, OpenDocumentTracker)>();
        foreach (var serverName in serverNames)
        {
            var pair = await GetOrStartNamedConnectionAsync(serverName, ct);
            if (pair.Connection is not null && pair.Tracker is not null)
            {
                results.Add((pair.Connection, pair.Tracker));
            }
        }
        return results;
    }

    private async Task<(LspConnection? Connection, OpenDocumentTracker? Tracker)> GetOrStartNamedConnectionAsync(string serverName, CancellationToken ct)
    {
        if (_connections.TryGetValue(serverName, out var existing) && !existing.IsFaulted)
        {
            await existing.WaitForReadyAsync(ct);
            _trackers.TryGetValue(serverName, out var existingTracker);
            return (existing, existingTracker);
        }

        var serverLock = _serverLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));
        await serverLock.WaitAsync(ct);
        try
        {
            if (_connections.TryGetValue(serverName, out existing) && !existing.IsFaulted)
            {
                await existing.WaitForReadyAsync(ct);
                _trackers.TryGetValue(serverName, out var existingTracker);
                return (existing, existingTracker);
            }

            // Clean up faulted connection
            if (existing is not null && existing.IsFaulted)
            {
                _connections.TryRemove(serverName, out _);
                if (_trackers.TryRemove(serverName, out var oldTracker))
                {
                    oldTracker.Dispose();
                }
                try { await existing.DisposeAsync(); } catch { /* best effort */ }

                var restartCount = _restartCounts.GetOrAdd(serverName, 0);
                if (restartCount >= MaxRestarts)
                {
                    return (null, null);
                }

                // Exponential backoff: 1s, 2s, 4s
                var delay = TimeSpan.FromSeconds(Math.Pow(2, restartCount));
                await Task.Delay(delay, ct);
                _restartCounts[serverName] = restartCount + 1;
                    NotifyServerStatus(serverName, false, true);
            }

            var lspConfig = _effectiveConfig;
            if (!lspConfig.TryGetValue(serverName, out var serverConfig))
            {
                return (null, null);
            }

            if (serverConfig.Command is null || serverConfig.Command.Count == 0)
            {
                return (null, null);
            }

            var connection = await LspConnection.StartAsync(
                serverConfig.Command,
                serverConfig.Env,
                _workingDirectory,
                serverConfig.Initialization,
                ct);

            if (connection is not null)
            {
                WireConnectionCallbacks(serverName, connection);
                _connections[serverName] = connection;
                _restartCounts[serverName] = 0; // Reset on successful start
                NotifyServerStatus(serverName, true, false);
                var tracker = new OpenDocumentTracker(connection);
                _trackers[serverName] = tracker;
                // Re-open documents that were open on the old connection would require
                // storing file paths — skipped for now since tracker is fresh
                return (connection, tracker);
            }

            return (null, null);
        }
        finally
        {
            serverLock.Release();
        }
    }

    private void WireConnectionCallbacks(string serverName, LspConnection connection)
    {
        connection.OnNotification = (method, @params) => HandleServerNotification(serverName, connection, method, @params);
        connection.OnServerRequest = (method, id, @params) => HandleServerRequest(connection, method, id, @params);
    }

    private void HandleServerNotification(string serverName, LspConnection connection, string method, System.Text.Json.JsonElement? @params)
    {
        switch (method)
        {
            case "window/logMessage":
            case "window/showMessage":
            {
                var msg = @params?.TryGetProperty("message", out var m) == true ? m.GetString() : null;
                var type = @params?.TryGetProperty("type", out var t) == true ? t.GetInt32() : 4;
                var logLevel = type switch
                {
                    1 => LogLevel.Error,
                    2 => LogLevel.Warning,
                    3 => LogLevel.Information,
                    _ => LogLevel.Debug,
                };
                _logger.Log(logLevel, "[{ServerName}] {Method}: {Message}", serverName, method, msg);
                break;
            }
            case "textDocument/publishDiagnostics":
            {
                HandlePublishDiagnostics(@params);
                break;
            }
            case "$/progress":
            {
                HandleProgress(@params);
                break;
            }
            default:
                _logger.LogDebug("[{ServerName}] Unhandled notification: {Method}", serverName, method);
                break;
        }
    }

    private Task<object?> HandleServerRequest(LspConnection connection, string method, int id, System.Text.Json.JsonElement? @params)
    {
        switch (method)
        {
            case "workspace/applyEdit":
            {
                var edit = @params is not null ? ParseWorkspaceEdit(@params) : null;
                if (edit is not null) _pendingEdits.Enqueue(edit);
                return Task.FromResult<object?>(new { applied = edit is not null });
            }
            case "window/workDoneProgress/create":
            {
                // Accept the progress token — no storage needed, we handle $/progress notifications
                return Task.FromResult<object?>(null);
            }
            case "client/registerCapability":
            {
                if (@params is not null &&
                    @params.Value.TryGetProperty("registrations", out var regs) &&
                    regs.ValueKind == JsonValueKind.Array)
                {
                    var patterns = new List<string>();
                    foreach (var reg in regs.EnumerateArray())
                    {
                        // Store dynamic registration for capability checking
                        var regId = reg.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        var regMethod = reg.TryGetProperty("method", out var meth) ? meth.GetString() : null;
                        if (regId is not null && regMethod is not null)
                        {
                            connection.RegisterCapability(regId, regMethod);
                        }

                        // Extract file watcher glob patterns if present
                        if (regMethod != "workspace/didChangeWatchedFiles") continue;
                        if (!reg.TryGetProperty("registerOptions", out var opts)) continue;
                        if (!opts.TryGetProperty("watchers", out var watchers)) continue;
                        foreach (var watcher in watchers.EnumerateArray())
                        {
                            if (watcher.TryGetProperty("globPattern", out var gp))
                            {
                                var pat = gp.GetString();
                                if (pat is not null) patterns.Add(pat);
                            }
                        }
                    }
                    if (patterns.Count > 0) _fileWatcher.SetPatterns(patterns);
                }
                return Task.FromResult<object?>(null);
            }
            case "client/unregisterCapability":
            {
                if (@params is not null &&
                    @params.Value.TryGetProperty("unregisterations", out var unregs) &&
                    unregs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var unreg in unregs.EnumerateArray())
                    {
                        var regId = unreg.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (regId is not null)
                        {
                            connection.UnregisterCapability(regId);
                        }
                    }
                }
                return Task.FromResult<object?>(null);
            }
            default:
                throw new LspMethodNotFoundException(method);
        }
    }

    private void OnFileChanges(IReadOnlyList<LspFileChange> changes)
    {
        var notifications = changes.Select(c => new
        {
            uri = FileToUri(c.FilePath),
            type = c.ChangeType,
        }).ToArray();

        var notification = new { changes = notifications };
        // Fire-and-forget: send to all active connections
        foreach (var conn in _connections.Values)
        {
            _ = conn.SendNotificationAsync("workspace/didChangeWatchedFiles", notification);
        }
    }

    /// <summary>
    /// Attempts to dequeue a pending workspace edit queued by a server-initiated workspace/applyEdit request.
    /// Returns true and sets <paramref name="edit"/> if an edit is available; otherwise returns false.
    /// </summary>
    internal bool TryDequeuePendingEdit(out LspWorkspaceEdit? edit) => _pendingEdits.TryDequeue(out edit);

    private void HandleProgress(JsonElement? @params)
    {
        if (@params is null) return;
        var token = @params.Value.TryGetProperty("token", out var t)
            ? (t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString())
            : null;
        if (token is null) return;

        if (!@params.Value.TryGetProperty("value", out var value)) return;

        var kind = value.TryGetProperty("kind", out var k) ? k.GetString() ?? "report" : "report";
        var title = value.TryGetProperty("title", out var ti) ? ti.GetString() : null;
        var message = value.TryGetProperty("message", out var m) ? m.GetString() : null;
        int? percentage = value.TryGetProperty("percentage", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : null;

        _onProgress?.Invoke(new LspProgressValue
        {
            Token = token,
            Kind = kind,
            Title = title,
            Message = message,
            Percentage = percentage,
        });
    }

    private void HandlePublishDiagnostics(System.Text.Json.JsonElement? @params)
    {
        if (@params is null) return;

        if (!@params.Value.TryGetProperty("uri", out var uriProp)) return;
        var uri = uriProp.GetString();
        if (uri is null) return;

        var filePath = UriToFile(uri);

        if (!@params.Value.TryGetProperty("diagnostics", out var diagArray) ||
            diagArray.ValueKind != JsonValueKind.Array)
        {
            _diagnosticsCache[filePath] = [];
            return;
        }

        var diagnostics = new List<LspDiagnostic>();
        foreach (var item in diagArray.EnumerateArray())
        {
            var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var severity = item.TryGetProperty("severity", out var s)
                ? (LspDiagnosticSeverity)s.GetInt32()
                : LspDiagnosticSeverity.Error;
            var source = item.TryGetProperty("source", out var src) ? src.GetString() : null;
            string? code = null;
            if (item.TryGetProperty("code", out var c))
            {
                code = c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString() : c.GetString();
            }

            LspLocation? range = null;
            if (item.TryGetProperty("range", out var rangeEl))
            {
                var start = rangeEl.GetProperty("start");
                var end = rangeEl.GetProperty("end");
                range = new LspLocation
                {
                    FilePath = filePath,
                    StartLine = start.GetProperty("line").GetInt32(),
                    StartCharacter = start.GetProperty("character").GetInt32(),
                    EndLine = end.GetProperty("line").GetInt32(),
                    EndCharacter = end.GetProperty("character").GetInt32(),
                };
            }

            if (range is null) continue;

            diagnostics.Add(new LspDiagnostic
            {
                FilePath = filePath,
                StartLine = range.StartLine,
                StartCharacter = range.StartCharacter,
                EndLine = range.EndLine,
                EndCharacter = range.EndCharacter,
                Severity = severity,
                Message = message,
                Source = source,
                Code = code,
            });
        }

        _diagnosticsCache[filePath] = diagnostics;
    }

    public async Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken cancellationToken)
    {
        ValidateFilePath(filePath);
        // Capture any existing push diagnostics before pull results overwrite the cache
        _diagnosticsCache.TryGetValue(filePath, out var pushCachedBeforePull);

        // Attempt pull diagnostics from all connections that support it
        var connections = await GetOrStartConnectionsAsync(filePath, cancellationToken);
        var pullResults = new List<LspDiagnostic>();
        var hasPull = false;

        foreach (var (conn, tracker) in connections)
        {
            if (!conn.SupportsCapability("textDocument/diagnostic")) continue;

            await EnsureOpenAsync(tracker, filePath, cancellationToken);

            var connServerName = _connections.FirstOrDefault(kvp => kvp.Value == conn).Key ?? conn.GetHashCode().ToString();
            var serverKey = $"{connServerName}|{filePath}";
            _diagnosticsResultId.TryGetValue(serverKey, out var previousResultId);

            var pullParams = previousResultId is not null
                ? (object)new { textDocument = new { uri = FileToUri(filePath) }, previousResultId }
                : new { textDocument = new { uri = FileToUri(filePath) }, previousResultId = (string?)null };

            var result = await conn.SendRequestAsync("textDocument/diagnostic", pullParams, cancellationToken);
            var report = ParseDiagnosticReport(result, filePath);

            if (report is not null)
            {
                hasPull = true;
                if (report.Kind == "full")
                {
                    pullResults.AddRange(report.Items);
                    // Update cache with pull results
                    _diagnosticsCache[filePath] = report.Items;
                }
                if (report.ResultId is not null)
                {
                    _diagnosticsResultId[serverKey] = report.ResultId;
                }
            }
        }

        if (hasPull)
        {
            // Merge pull results with push diagnostics captured before pull overwrote the cache
            if (pushCachedBeforePull is not null && pushCachedBeforePull.Count > 0)
            {
                var merged = new List<LspDiagnostic>(pullResults);
                var seen = new HashSet<(int, int, string, LspDiagnosticSeverity)>(
                    pullResults.Select(d => (d.StartLine, d.StartCharacter, d.Message, d.Severity)));
                foreach (var d in pushCachedBeforePull)
                {
                    if (seen.Add((d.StartLine, d.StartCharacter, d.Message, d.Severity)))
                    {
                        merged.Add(d);
                    }
                }
                return merged;
            }
            return pullResults;
        }

        // Fall back to push cache
        if (_diagnosticsCache.TryGetValue(filePath, out var cached))
        {
            return cached;
        }
        return [];
    }

    private static LspDiagnosticReport? ParseDiagnosticReport(JsonElement? result, string filePath)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;

        var kind = result.Value.TryGetProperty("kind", out var k) ? k.GetString() ?? "full" : "full";
        var resultId = result.Value.TryGetProperty("resultId", out var rid) ? rid.GetString() : null;

        if (kind == "unchanged") return new LspDiagnosticReport { Kind = "unchanged", Items = [], ResultId = resultId };

        var items = new List<LspDiagnostic>();
        if (result.Value.TryGetProperty("items", out var diagArray) && diagArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in diagArray.EnumerateArray())
            {
                var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var severity = item.TryGetProperty("severity", out var s)
                    ? (LspDiagnosticSeverity)s.GetInt32()
                    : LspDiagnosticSeverity.Error;
                var source = item.TryGetProperty("source", out var src) ? src.GetString() : null;
                string? code = null;
                if (item.TryGetProperty("code", out var c))
                {
                    code = c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString() : c.GetString();
                }

                if (!item.TryGetProperty("range", out var rangeEl)) continue;
                var start = rangeEl.GetProperty("start");
                var end = rangeEl.GetProperty("end");

                items.Add(new LspDiagnostic
                {
                    FilePath = filePath,
                    StartLine = start.GetProperty("line").GetInt32(),
                    StartCharacter = start.GetProperty("character").GetInt32(),
                    EndLine = end.GetProperty("line").GetInt32(),
                    EndCharacter = end.GetProperty("character").GetInt32(),
                    Severity = severity,
                    Message = message,
                    Source = source,
                    Code = code,
                });
            }
        }

        return new LspDiagnosticReport { Kind = kind, Items = items, ResultId = resultId };
    }

    public async Task NotifyDocumentChangedAsync(string filePath, string newText, CancellationToken cancellationToken)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, cancellationToken);
        foreach (var (_, tracker) in connections)
        {
            await tracker.SendChangeAsync(filePath, newText, cancellationToken);
        }
    }

    public async Task<LspWorkspaceEdit?> ApplyCodeActionAsync(
        string filePath, int startLine, int startCharacter, int endLine, int endCharacter,
        int codeActionIndex, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var actions = await CodeActionAsync(filePath, startLine, startCharacter, endLine, endCharacter, ct);
        if (codeActionIndex < 0 || codeActionIndex >= actions.Count) return null;

        // Try codeAction/resolve on the first connection that supports it
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        foreach (var (conn, _) in connections)
        {
            if (!conn.SupportsCapability("codeAction/resolve")) continue;

            var resolved = await conn.SendRequestAsync("codeAction/resolve", new
            {
                title = actions[codeActionIndex].Title,
                kind = actions[codeActionIndex].Kind,
            }, ct);

            // Resolved action may carry an inline edit
            if (resolved is not null && resolved.Value.TryGetProperty("edit", out var editEl))
            {
                var edit = ParseWorkspaceEdit(editEl);
                if (edit is not null) return edit;
            }

            // Otherwise wait briefly for a workspace/applyEdit callback (up to 5s)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                if (TryDequeuePendingEdit(out var pending)) return pending;
                await Task.Delay(50, timeoutCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            return null;
        }

        return null;
    }

    public async Task<IReadOnlyList<LspCompletionItem>> CompletionAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/completion", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/completion", CreatePositionParams(filePath, line, character), ct);
            return ParseCompletionItems(result);
        });
    }

    public async Task<IReadOnlyList<LspCodeAction>> CodeActionAsync(
        string filePath, int startLine, int startCharacter, int endLine, int endCharacter, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        var diagnostics = await GetDiagnosticsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/codeAction", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/codeAction", new
            {
                textDocument = new { uri = FileToUri(filePath) },
                range = new
                {
                    start = new { line = startLine, character = startCharacter },
                    end   = new { line = endLine,   character = endCharacter }
                },
                context = new
                {
                    diagnostics = diagnostics.Select(d => new
                    {
                        message  = d.Message,
                        severity = (int)d.Severity,
                    }).ToArray()
                }
            }, ct);
            return ParseCodeActions(result);
        });
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormattingAsync(string filePath, int tabSize, bool insertSpaces, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/formatting", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/formatting", new
            {
                textDocument = new { uri = FileToUri(filePath) },
                options = new { tabSize, insertSpaces }
            }, ct);
            return ParseTextEdits(result);
        });
    }

    public async Task<LspWorkspaceEdit?> RenameAsync(string filePath, int line, int character, string newName, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await FirstNonNullAsync(connections, "textDocument/rename", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/rename", new
            {
                textDocument = new { uri = FileToUri(filePath) },
                position = new { line, character },
                newName
            }, ct);
            return ParseWorkspaceEdit(result);
        });
    }

    public async Task<LspSignatureHelp?> SignatureHelpAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await FirstNonNullAsync(connections, "textDocument/signatureHelp", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/signatureHelp", CreatePositionParams(filePath, line, character), ct);
            return ParseSignatureHelp(result);
        });
    }

    public async Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/definition", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/definition", CreatePositionParams(filePath, line, character), ct);
            return ParseLocations(result);
        });
    }

    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/references", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/references", new
            {
                textDocument = new { uri = FileToUri(filePath) },
                position = new { line, character },
                context = new { includeDeclaration = true },
            }, ct);
            return ParseLocations(result);
        });
    }

    public async Task<LspHoverResult?> HoverAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await FirstNonNullAsync(connections, "textDocument/hover", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/hover", CreatePositionParams(filePath, line, character), ct);
            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;

            var content = "";
            if (result.Value.TryGetProperty("contents", out var contents))
            {
                content = contents.ValueKind switch
                {
                    JsonValueKind.String => contents.GetString() ?? "",
                    JsonValueKind.Object when contents.TryGetProperty("value", out var v) => v.GetString() ?? "",
                    _ => contents.ToString(),
                };
            }

            return new LspHoverResult { Content = content };
        });
    }

    public async Task<IReadOnlyList<LspSymbol>> DocumentSymbolAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/documentSymbol", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/documentSymbol", new
            {
                textDocument = new { uri = FileToUri(filePath) },
            }, ct);
            return ParseSymbols(result, filePath);
        });
    }

    public async Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(string query, CancellationToken ct)
    {
        // Query all active connections that support workspace/symbol
        var all = new List<LspSymbol>();
        foreach (var conn in _connections.Values)
        {
            if (!conn.SupportsCapability("workspace/symbol")) continue;
            var result = await conn.SendRequestAsync("workspace/symbol", new { query }, ct);
            all.AddRange(ParseSymbols(result, null));
        }
        return all;
    }

    public async Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/implementation", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/implementation", CreatePositionParams(filePath, line, character), ct);
            return ParseLocations(result);
        });
    }

    public async Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/prepareCallHierarchy", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/prepareCallHierarchy", CreatePositionParams(filePath, line, character), ct);
            return ParseCallHierarchyItems(result);
        });
    }

    public async Task<IReadOnlyList<LspCallHierarchyItem>> IncomingCallsAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "callHierarchy/incomingCalls", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var prepared = await conn.SendRequestAsync("textDocument/prepareCallHierarchy", CreatePositionParams(filePath, line, character), ct);
            var items = ParseCallHierarchyItems(prepared);
            if (items.Count == 0) return [];
            var result = await conn.SendRequestAsync("callHierarchy/incomingCalls", new { item = items[0] }, ct);
            return ParseCallHierarchyCallItems(result);
        });
    }

    public async Task<IReadOnlyList<LspCallHierarchyItem>> OutgoingCallsAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "callHierarchy/outgoingCalls", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var prepared = await conn.SendRequestAsync("textDocument/prepareCallHierarchy", CreatePositionParams(filePath, line, character), ct);
            var items = ParseCallHierarchyItems(prepared);
            if (items.Count == 0) return [];
            var result = await conn.SendRequestAsync("callHierarchy/outgoingCalls", new { item = items[0] }, ct);
            return ParseCallHierarchyCallItems(result);
        });
    }

    public async Task<IReadOnlyList<LspTypeHierarchyItem>> PrepareTypeHierarchyAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/prepareTypeHierarchy", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/prepareTypeHierarchy", CreatePositionParams(filePath, line, character), ct);
            return ParseTypeHierarchyItems(result);
        });
    }

    public async Task<IReadOnlyList<LspTypeHierarchyItem>> SupertypesAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "typeHierarchy/supertypes", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var prepared = await conn.SendRequestAsync("textDocument/prepareTypeHierarchy", CreatePositionParams(filePath, line, character), ct);
            var items = ParseTypeHierarchyItems(prepared);
            if (items.Count == 0) return [];
            var result = await conn.SendRequestAsync("typeHierarchy/supertypes", new { item = items[0] }, ct);
            return ParseTypeHierarchyItems(result);
        });
    }

    public async Task<IReadOnlyList<LspTypeHierarchyItem>> SubtypesAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "typeHierarchy/subtypes", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var prepared = await conn.SendRequestAsync("textDocument/prepareTypeHierarchy", CreatePositionParams(filePath, line, character), ct);
            var items = ParseTypeHierarchyItems(prepared);
            if (items.Count == 0) return [];
            var result = await conn.SendRequestAsync("typeHierarchy/subtypes", new { item = items[0] }, ct);
            return ParseTypeHierarchyItems(result);
        });
    }

    public async Task<IReadOnlyList<LspDocumentLink>> DocumentLinkAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        var allLinks = new List<LspDocumentLink>();

        foreach (var (conn, tracker) in connections)
        {
            if (!conn.SupportsCapability("textDocument/documentLink")) continue;
            await EnsureOpenAsync(tracker, filePath, ct);

            var result = await conn.SendRequestAsync("textDocument/documentLink", new
            {
                textDocument = new { uri = FileToUri(filePath) },
            }, ct);

            var links = ParseDocumentLinksWithRaw(result);
            foreach (var (link, rawElement) in links)
            {
                LspDocumentLink finalLink;
                if (link.Target is null && SupportsDocumentLinkResolve(conn))
                {
                    var resolved = await conn.SendRequestAsync("documentLink/resolve", rawElement, ct);
                    finalLink = ParseSingleDocumentLink(resolved) ?? link;
                }
                else
                {
                    finalLink = link;
                }

                // Filter: only allow http, https, or file scheme targets
                if (string.IsNullOrEmpty(finalLink.Target)) continue;
                if (!Uri.TryCreate(finalLink.Target, UriKind.Absolute, out var targetUri)) continue;
                if (targetUri.Scheme is not ("http" or "https" or "file")) continue;

                allLinks.Add(finalLink);
            }
        }

        return allLinks;
    }

    private static bool SupportsDocumentLinkResolve(LspConnection conn)
    {
        if (conn.ServerCapabilities is null) return false;
        if (!conn.ServerCapabilities.Value.TryGetProperty("documentLinkProvider", out var provider)) return false;
        if (provider.ValueKind != JsonValueKind.Object) return false;
        return provider.TryGetProperty("resolveProvider", out var rp) && rp.ValueKind == JsonValueKind.True;
    }

    private static Task EnsureOpenAsync(OpenDocumentTracker? tracker, string filePath, CancellationToken ct)
    {
        if (tracker is null) return Task.CompletedTask;
        return tracker.EnsureOpenAsync(filePath, ct);
    }

    private static async Task<IReadOnlyList<T>> AggregateListsAsync<T>(
        IReadOnlyList<(LspConnection Connection, OpenDocumentTracker Tracker)> connections,
        string method,
        Func<LspConnection, OpenDocumentTracker, Task<IReadOnlyList<T>>> operation)
    {
        if (connections.Count == 0) return [];
        var supporting = connections.Where(c => c.Connection.SupportsCapability(method)).ToList();
        if (supporting.Count == 0) return [];
        if (supporting.Count == 1) return await operation(supporting[0].Connection, supporting[0].Tracker);
        var tasks = supporting.Select(c => operation(c.Connection, c.Tracker)).ToList();
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private static async Task<T?> FirstNonNullAsync<T>(
        IReadOnlyList<(LspConnection Connection, OpenDocumentTracker Tracker)> connections,
        string method,
        Func<LspConnection, OpenDocumentTracker, Task<T?>> operation) where T : class
    {
        foreach (var (conn, tracker) in connections)
        {
            if (!conn.SupportsCapability(method)) continue;
            var result = await operation(conn, tracker);
            if (result is not null) return result;
        }
        return null;
    }

    private static object CreatePositionParams(string filePath, int line, int character) => new
    {
        textDocument = new { uri = FileToUri(filePath) },
        position = new { line, character },
    };

    private static string FileToUri(string filePath) => new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

    private static string UriToFile(string uri)
    {
        try { return new Uri(uri).LocalPath; }
        catch { return uri; }
    }

    private static IReadOnlyList<LspCompletionItem> ParseCompletionItems(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return [];

        // Response may be an array or a CompletionList { items: [...] }
        JsonElement itemsEl;
        if (result.Value.ValueKind == JsonValueKind.Array)
        {
            itemsEl = result.Value;
        }
        else if (result.Value.ValueKind == JsonValueKind.Object &&
                 result.Value.TryGetProperty("items", out var items))
        {
            itemsEl = items;
        }
        else return [];

        var list = new List<LspCompletionItem>();
        foreach (var item in itemsEl.EnumerateArray())
        {
            var label = item.TryGetProperty("label", out var l) ? l.GetString() : null;
            if (label is null) continue;

            var kind = item.TryGetProperty("kind", out var k)
                ? (LspCompletionItemKind)k.GetInt32()
                : LspCompletionItemKind.Text;

            var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var insertText = item.TryGetProperty("insertText", out var it) ? it.GetString() : null;
            var filterText = item.TryGetProperty("filterText", out var ft) ? ft.GetString() : null;
            var sortText = item.TryGetProperty("sortText", out var st) ? st.GetString() : null;

            list.Add(new LspCompletionItem
            {
                Label = label,
                Kind = kind,
                Detail = detail,
                InsertText = insertText,
                FilterText = filterText,
                SortText = sortText,
            });
        }
        return list;
    }

    private static IReadOnlyList<LspCodeAction> ParseCodeActions(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return [];

        var list = new List<LspCodeAction>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title is null) continue;

            var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var isPreferred = item.TryGetProperty("isPreferred", out var ip) && ip.GetBoolean();

            list.Add(new LspCodeAction
            {
                Title = title,
                Kind = kind,
                IsPreferred = isPreferred,
            });
        }
        return list;
    }

    private static IReadOnlyList<LspTextEdit> ParseTextEdits(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return [];

        var list = new List<LspTextEdit>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (!item.TryGetProperty("range", out var range)) continue;
            if (!item.TryGetProperty("newText", out var nt)) continue;

            var start = range.GetProperty("start");
            var end   = range.GetProperty("end");

            list.Add(new LspTextEdit
            {
                StartLine      = start.GetProperty("line").GetInt32(),
                StartCharacter = start.GetProperty("character").GetInt32(),
                EndLine        = end.GetProperty("line").GetInt32(),
                EndCharacter   = end.GetProperty("character").GetInt32(),
                NewText        = nt.GetString() ?? string.Empty,
            });
        }
        return list;
    }

    private static LspWorkspaceEdit? ParseWorkspaceEdit(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
        if (!result.Value.TryGetProperty("changes", out var changes)) return null;

        var dict = new Dictionary<string, IReadOnlyList<LspTextEdit>>();
        foreach (var prop in changes.EnumerateObject())
        {
            var edits = new List<LspTextEdit>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("range", out var range)) continue;
                if (!item.TryGetProperty("newText", out var nt)) continue;

                var start = range.GetProperty("start");
                var end   = range.GetProperty("end");

                edits.Add(new LspTextEdit
                {
                    StartLine      = start.GetProperty("line").GetInt32(),
                    StartCharacter = start.GetProperty("character").GetInt32(),
                    EndLine        = end.GetProperty("line").GetInt32(),
                    EndCharacter   = end.GetProperty("character").GetInt32(),
                    NewText        = nt.GetString() ?? string.Empty,
                });
            }
            dict[UriToFile(prop.Name)] = edits;
        }
        return new LspWorkspaceEdit { Changes = dict };
    }

    private static LspSignatureHelp? ParseSignatureHelp(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
        if (!result.Value.TryGetProperty("signatures", out var sigs)) return null;

        var signatures = new List<LspSignatureInformation>();
        foreach (var sig in sigs.EnumerateArray())
        {
            var label = sig.TryGetProperty("label", out var l) ? l.GetString() : null;
            if (label is null) continue;

            var doc = sig.TryGetProperty("documentation", out var d) ? d.GetString() : null;

            List<LspParameterInformation>? parameters = null;
            if (sig.TryGetProperty("parameters", out var parms))
            {
                parameters = [];
                foreach (var p in parms.EnumerateArray())
                {
                    var pLabel = p.TryGetProperty("label", out var pl) ? pl.GetString() : null;
                    if (pLabel is null) continue;
                    var pDoc = p.TryGetProperty("documentation", out var pd) ? pd.GetString() : null;
                    parameters.Add(new LspParameterInformation { Label = pLabel, Documentation = pDoc });
                }
            }

            signatures.Add(new LspSignatureInformation
            {
                Label         = label,
                Documentation = doc,
                Parameters    = parameters,
            });
        }

        int? activeSig = result.Value.TryGetProperty("activeSignature", out var as_) ? as_.GetInt32() : null;
        int? activeParam = result.Value.TryGetProperty("activeParameter", out var ap) ? ap.GetInt32() : null;

        return new LspSignatureHelp
        {
            Signatures      = signatures,
            ActiveSignature = activeSig,
            ActiveParameter = activeParam,
        };
    }

    private static IReadOnlyList<LspLocation> ParseLocations(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return [];

        var locations = new List<LspLocation>();

        if (result.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.Value.EnumerateArray())
            {
                var loc = ParseSingleLocation(item);
                if (loc is not null) locations.Add(loc);
            }
        }
        else if (result.Value.ValueKind == JsonValueKind.Object)
        {
            var loc = ParseSingleLocation(result.Value);
            if (loc is not null) locations.Add(loc);
        }

        return locations;
    }

    private static LspLocation? ParseSingleLocation(JsonElement item)
    {
        string? uri = null;
        JsonElement range;

        if (item.TryGetProperty("uri", out var uriProp))
        {
            uri = uriProp.GetString();
            if (!item.TryGetProperty("range", out range)) return null;
        }
        else if (item.TryGetProperty("targetUri", out var targetUri))
        {
            uri = targetUri.GetString();
            if (!item.TryGetProperty("targetRange", out range)) return null;
        }
        else return null;

        if (uri is null) return null;

        var start = range.GetProperty("start");
        var end = range.GetProperty("end");

        return new LspLocation
        {
            FilePath = UriToFile(uri),
            StartLine = start.GetProperty("line").GetInt32(),
            StartCharacter = start.GetProperty("character").GetInt32(),
            EndLine = end.GetProperty("line").GetInt32(),
            EndCharacter = end.GetProperty("character").GetInt32(),
        };
    }

    private static IReadOnlyList<LspSymbol> ParseSymbols(JsonElement? result, string? defaultFilePath)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var symbols = new List<LspSymbol>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var kind = item.TryGetProperty("kind", out var k) ? MapSymbolKind(k.GetInt32()) : "Unknown";

            LspLocation? location = null;
            if (item.TryGetProperty("location", out var loc))
            {
                location = ParseSingleLocation(loc);
            }
            else if (item.TryGetProperty("range", out var range) && defaultFilePath is not null)
            {
                var start = range.GetProperty("start");
                var end = range.GetProperty("end");
                location = new LspLocation
                {
                    FilePath = defaultFilePath,
                    StartLine = start.GetProperty("line").GetInt32(),
                    StartCharacter = start.GetProperty("character").GetInt32(),
                    EndLine = end.GetProperty("line").GetInt32(),
                    EndCharacter = end.GetProperty("character").GetInt32(),
                };
            }

            if (name is not null && location is not null)
            {
                var container = item.TryGetProperty("containerName", out var cn) ? cn.GetString() : null;
                symbols.Add(new LspSymbol { Name = name, Kind = kind, Location = location, ContainerName = container });
            }
        }

        return symbols;
    }

    private static IReadOnlyList<LspCallHierarchyItem> ParseCallHierarchyItems(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var items = new List<LspCallHierarchyItem>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var parsed = ParseSingleCallHierarchyItem(item);
            if (parsed is not null) items.Add(parsed);
        }
        return items;
    }

    private static IReadOnlyList<LspCallHierarchyItem> ParseCallHierarchyCallItems(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var items = new List<LspCallHierarchyItem>();
        foreach (var item in result.Value.EnumerateArray())
        {
            // CallHierarchyIncomingCall/OutgoingCall has "from"/"to" property
            JsonElement target;
            if (item.TryGetProperty("from", out target) || item.TryGetProperty("to", out target))
            {
                var parsed = ParseSingleCallHierarchyItem(target);
                if (parsed is not null) items.Add(parsed);
            }
        }
        return items;
    }

    private static LspCallHierarchyItem? ParseSingleCallHierarchyItem(JsonElement item)
    {
        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
        var kind = item.TryGetProperty("kind", out var k) ? MapSymbolKind(k.GetInt32()) : "Unknown";
        var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
        var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : null;

        if (name is null || uri is null) return null;

        LspLocation? location = null;
        if (item.TryGetProperty("range", out var range))
        {
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            location = new LspLocation
            {
                FilePath = UriToFile(uri),
                StartLine = start.GetProperty("line").GetInt32(),
                StartCharacter = start.GetProperty("character").GetInt32(),
                EndLine = end.GetProperty("line").GetInt32(),
                EndCharacter = end.GetProperty("character").GetInt32(),
            };
        }

        if (location is null) return null;

        return new LspCallHierarchyItem { Name = name, Kind = kind, Location = location, Detail = detail };
    }

    private static IReadOnlyList<LspTypeHierarchyItem> ParseTypeHierarchyItems(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var items = new List<LspTypeHierarchyItem>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var parsed = ParseSingleTypeHierarchyItem(item);
            if (parsed is not null) items.Add(parsed);
        }
        return items;
    }

    private static LspTypeHierarchyItem? ParseSingleTypeHierarchyItem(JsonElement item)
    {
        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
        var kind = item.TryGetProperty("kind", out var k) ? MapSymbolKind(k.GetInt32()) : "Unknown";
        var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
        var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : null;

        if (name is null || uri is null) return null;

        LspLocation? location = null;
        if (item.TryGetProperty("range", out var range))
        {
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            location = new LspLocation
            {
                FilePath = UriToFile(uri),
                StartLine = start.GetProperty("line").GetInt32(),
                StartCharacter = start.GetProperty("character").GetInt32(),
                EndLine = end.GetProperty("line").GetInt32(),
                EndCharacter = end.GetProperty("character").GetInt32(),
            };
        }

        if (location is null) return null;

        return new LspTypeHierarchyItem { Name = name, Kind = kind, Location = location, Detail = detail };
    }

    private static string MapSymbolKind(int kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package",
        5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field",
        9 => "Constructor", 10 => "Enum", 11 => "Interface", 12 => "Function",
        13 => "Variable", 14 => "Constant", 15 => "String", 16 => "Number",
        17 => "Boolean", 18 => "Array", 19 => "Object", 20 => "Key",
        21 => "Null", 22 => "EnumMember", 23 => "Struct", 24 => "Event",
        25 => "Operator", 26 => "TypeParameter",
        _ => "Unknown",
    };

    public async Task<LspSemanticTokens?> SemanticTokensAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await FirstNonNullAsync(connections, "textDocument/semanticTokens/full", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/semanticTokens/full", new
            {
                textDocument = new { uri = FileToUri(filePath) },
            }, ct);
            return ParseSemanticTokens(result);
        });
    }

    public async Task<LspSemanticTokensLegend?> GetSemanticTokensLegendAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        if (_cachedLegend is not null) return _cachedLegend;

        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        foreach (var (conn, _) in connections)
        {
            if (!conn.SupportsCapability("textDocument/semanticTokens/full")) continue;
            if (conn.ServerCapabilities is null) continue;

            if (conn.ServerCapabilities.Value.TryGetProperty("semanticTokensProvider", out var provider) &&
                provider.TryGetProperty("legend", out var legend))
            {
                var tokenTypes = new List<string>();
                if (legend.TryGetProperty("tokenTypes", out var tt) && tt.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tt.EnumerateArray())
                    {
                        tokenTypes.Add(t.GetString() ?? "");
                    }
                }

                var tokenModifiers = new List<string>();
                if (legend.TryGetProperty("tokenModifiers", out var tm) && tm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in tm.EnumerateArray())
                    {
                        tokenModifiers.Add(m.GetString() ?? "");
                    }
                }

                _cachedLegend = new LspSemanticTokensLegend
                {
                    TokenTypes = tokenTypes,
                    TokenModifiers = tokenModifiers,
                };
                return _cachedLegend;
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<LspInlayHint>> InlayHintAsync(
        string filePath, int startLine, int startCharacter, int endLine, int endCharacter, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/inlayHint", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/inlayHint", new
            {
                textDocument = new { uri = FileToUri(filePath) },
                range = new
                {
                    start = new { line = startLine, character = startCharacter },
                    end = new { line = endLine, character = endCharacter },
                },
            }, ct);
            return ParseInlayHints(result);
        });
    }

    public async Task<IReadOnlyList<LspCodeLens>> CodeLensAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        var allLenses = new List<LspCodeLens>();

        foreach (var (conn, tracker) in connections)
        {
            if (!conn.SupportsCapability("textDocument/codeLens")) continue;
            await EnsureOpenAsync(tracker, filePath, ct);

            var result = await conn.SendRequestAsync("textDocument/codeLens", new
            {
                textDocument = new { uri = FileToUri(filePath) },
            }, ct);

            var lenses = ParseCodeLenses(result);
            foreach (var lens in lenses)
            {
                if (!lens.IsResolved && conn.SupportsCapability("codeLens/resolve"))
                {
                    var resolved = await conn.SendRequestAsync("codeLens/resolve", new
                    {
                        range = new
                        {
                            start = new { line = lens.StartLine, character = lens.StartCharacter },
                            end = new { line = lens.EndLine, character = lens.EndCharacter },
                        },
                    }, ct);
                    var resolvedLens = ParseSingleCodeLens(resolved);
                    allLenses.Add(resolvedLens ?? lens);
                }
                else
                {
                    allLenses.Add(lens);
                }
            }
        }

        return allLenses;
    }

    public async Task<IReadOnlyList<LspFoldingRange>> FoldingRangeAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/foldingRange", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/foldingRange", new
            {
                textDocument = new { uri = FileToUri(filePath) },
            }, ct);
            return ParseFoldingRanges(result);
        });
    }

    public async Task<IReadOnlyList<LspSelectionRange>> SelectionRangeAsync(
        string filePath, IReadOnlyList<(int Line, int Character)> positions, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/selectionRange", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/selectionRange", new
            {
                textDocument = new { uri = FileToUri(filePath) },
                positions = positions.Select(p => new { line = p.Line, character = p.Character }).ToArray(),
            }, ct);
            return ParseSelectionRanges(result);
        });
    }

    public void OnProgress(Action<LspProgressValue> handler) => _onProgress += handler;

    public async Task<IReadOnlyList<LspLocation>> GoToTypeDefinitionAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/typeDefinition", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/typeDefinition", CreatePositionParams(filePath, line, character), ct);
            return ParseLocations(result);
        });
    }

    public async Task<IReadOnlyList<LspLocation>> GoToDeclarationAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/declaration", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/declaration", CreatePositionParams(filePath, line, character), ct);
            return ParseLocations(result);
        });
    }

    public async Task<IReadOnlyList<LspDocumentHighlight>> DocumentHighlightAsync(string filePath, int line, int character, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        var connections = await GetOrStartConnectionsAsync(filePath, ct);
        return await AggregateListsAsync(connections, "textDocument/documentHighlight", async (conn, tracker) =>
        {
            await EnsureOpenAsync(tracker, filePath, ct);
            var result = await conn.SendRequestAsync("textDocument/documentHighlight", CreatePositionParams(filePath, line, character), ct);
            return ParseDocumentHighlights(result);
        });
    }

    public async Task<string?> ExecuteCommandAsync(string command, IReadOnlyList<object>? arguments, CancellationToken ct)
    {
        foreach (var conn in _connections.Values)
        {
            if (!conn.SupportsCapability("workspace/executeCommand")) continue;
            if (!IsCommandAdvertised(conn, command)) continue;

            var result = await conn.SendRequestAsync("workspace/executeCommand", new
            {
                command,
                arguments = arguments ?? Array.Empty<object>(),
            }, ct);
            return result?.ToString();
        }
        return null;
    }

    private static bool IsCommandAdvertised(LspConnection connection, string command)
    {
        if (connection.ServerCapabilities is null) return true; // optimistic
        if (!connection.ServerCapabilities.Value.TryGetProperty("executeCommandProvider", out var provider)) return false;
        if (!provider.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return false;

        foreach (var cmd in commands.EnumerateArray())
        {
            if (cmd.ValueKind == JsonValueKind.String && cmd.GetString() == command) return true;
        }
        return false;
    }

    private static IReadOnlyList<LspDocumentHighlight> ParseDocumentHighlights(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var highlights = new List<LspDocumentHighlight>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (!item.TryGetProperty("range", out var range)) continue;
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");

            var kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number
                ? (LspDocumentHighlightKind)k.GetInt32()
                : LspDocumentHighlightKind.Text;

            highlights.Add(new LspDocumentHighlight
            {
                StartLine = start.GetProperty("line").GetInt32(),
                StartCharacter = start.GetProperty("character").GetInt32(),
                EndLine = end.GetProperty("line").GetInt32(),
                EndCharacter = end.GetProperty("character").GetInt32(),
                Kind = kind,
            });
        }
        return highlights;
    }

    public void OnServerStatusChanged(Action<LspServerStatus> handler) => _onServerStatusChanged += handler;

    public async Task<IReadOnlyList<LspServerStatus>> GetServerStatusAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var statuses = new List<LspServerStatus>();
        IDictionary<string, LspServerConfig> lspConfig = _effectiveConfig.Count > 0 
            ? _effectiveConfig 
            : _configMonitor.CurrentValue.Lsp ?? (IDictionary<string, LspServerConfig>)new Dictionary<string, LspServerConfig>();
        foreach (var (name, config) in lspConfig)
        {
            if (config.Disabled == true) continue;
            var hasConnection = _connections.TryGetValue(name, out var conn);
            var isFaulted = hasConnection && conn!.IsFaulted;
            _restartCounts.TryGetValue(name, out var restartCount);

            statuses.Add(new LspServerStatus
            {
                ServerName = name,
                IsRunning = hasConnection && !isFaulted,
                IsFaulted = isFaulted,
                RestartCount = restartCount,
                MaxRestarts = MaxRestarts,
            });
        }
        return statuses;
    }

    private void NotifyServerStatus(string serverName, bool isRunning, bool isFaulted)
    {
        _restartCounts.TryGetValue(serverName, out var restartCount);
        _onServerStatusChanged?.Invoke(new LspServerStatus
        {
            ServerName = serverName,
            IsRunning = isRunning,
            IsFaulted = isFaulted,
            RestartCount = restartCount,
            MaxRestarts = MaxRestarts,
        });
    }

    private static LspSemanticTokens? ParseSemanticTokens(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;

        var data = new List<int>();
        if (result.Value.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                data.Add(item.GetInt32());
            }
        }

        var resultId = result.Value.TryGetProperty("resultId", out var rid) ? rid.GetString() : null;
        return new LspSemanticTokens { Data = data, ResultId = resultId };
    }

    private static IReadOnlyList<LspInlayHint> ParseInlayHints(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var hints = new List<LspInlayHint>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (!item.TryGetProperty("position", out var pos)) continue;
            var line = pos.GetProperty("line").GetInt32();
            var character = pos.GetProperty("character").GetInt32();

            string? label = null;
            if (item.TryGetProperty("label", out var labelEl))
            {
                label = labelEl.ValueKind == JsonValueKind.String
                    ? labelEl.GetString()
                    : labelEl.ToString();
            }
            if (label is null) continue;

            LspInlayHintKind? kind = null;
            if (item.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.Number)
            {
                kind = (LspInlayHintKind)kindEl.GetInt32();
            }

            var paddingLeft = item.TryGetProperty("paddingLeft", out var pl) && pl.GetBoolean();
            var paddingRight = item.TryGetProperty("paddingRight", out var pr) && pr.GetBoolean();

            hints.Add(new LspInlayHint
            {
                Line = line,
                Character = character,
                Label = label,
                Kind = kind,
                PaddingLeft = paddingLeft,
                PaddingRight = paddingRight,
            });
        }
        return hints;
    }

    private static IReadOnlyList<LspCodeLens> ParseCodeLenses(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var lenses = new List<LspCodeLens>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var lens = ParseSingleCodeLensFromElement(item);
            if (lens is not null) lenses.Add(lens);
        }
        return lenses;
    }

    private static LspCodeLens? ParseSingleCodeLens(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
        return ParseSingleCodeLensFromElement(result.Value);
    }

    private static LspCodeLens? ParseSingleCodeLensFromElement(JsonElement item)
    {
        if (!item.TryGetProperty("range", out var range)) return null;
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");

        string? commandTitle = null;
        string? commandName = null;
        var isResolved = false;

        if (item.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.Object)
        {
            commandTitle = cmd.TryGetProperty("title", out var t) ? t.GetString() : null;
            commandName = cmd.TryGetProperty("command", out var c) ? c.GetString() : null;
            isResolved = true;
        }

        return new LspCodeLens
        {
            StartLine = start.GetProperty("line").GetInt32(),
            StartCharacter = start.GetProperty("character").GetInt32(),
            EndLine = end.GetProperty("line").GetInt32(),
            EndCharacter = end.GetProperty("character").GetInt32(),
            CommandTitle = commandTitle,
            CommandName = commandName,
            IsResolved = isResolved,
        };
    }

    private static IReadOnlyList<LspFoldingRange> ParseFoldingRanges(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var ranges = new List<LspFoldingRange>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var startLine = item.TryGetProperty("startLine", out var sl) ? sl.GetInt32() : 0;
            var endLine = item.TryGetProperty("endLine", out var el) ? el.GetInt32() : 0;
            int? startChar = item.TryGetProperty("startCharacter", out var sc) ? sc.GetInt32() : null;
            int? endChar = item.TryGetProperty("endCharacter", out var ec) ? ec.GetInt32() : null;
            var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;

            ranges.Add(new LspFoldingRange
            {
                StartLine = startLine,
                StartCharacter = startChar,
                EndLine = endLine,
                EndCharacter = endChar,
                Kind = kind,
            });
        }
        return ranges;
    }

    private static IReadOnlyList<LspSelectionRange> ParseSelectionRanges(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var ranges = new List<LspSelectionRange>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var parsed = ParseSingleSelectionRange(item);
            if (parsed is not null) ranges.Add(parsed);
        }
        return ranges;
    }

    private static LspSelectionRange? ParseSingleSelectionRange(JsonElement item)
    {
        if (!item.TryGetProperty("range", out var range)) return null;
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");

        LspSelectionRange? parent = null;
        if (item.TryGetProperty("parent", out var parentEl) && parentEl.ValueKind == JsonValueKind.Object)
        {
            parent = ParseSingleSelectionRange(parentEl);
        }

        return new LspSelectionRange
        {
            StartLine = start.GetProperty("line").GetInt32(),
            StartCharacter = start.GetProperty("character").GetInt32(),
            EndLine = end.GetProperty("line").GetInt32(),
            EndCharacter = end.GetProperty("character").GetInt32(),
            Parent = parent,
        };
    }

    private static IReadOnlyList<(LspDocumentLink Link, JsonElement RawElement)> ParseDocumentLinksWithRaw(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

        var links = new List<(LspDocumentLink, JsonElement)>();
        foreach (var item in result.Value.EnumerateArray())
        {
            var link = ParseSingleDocumentLinkFromElement(item);
            if (link is not null) links.Add((link, item));
        }
        return links;
    }

    private static LspDocumentLink? ParseSingleDocumentLink(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
        return ParseSingleDocumentLinkFromElement(result.Value);
    }

    private static LspDocumentLink? ParseSingleDocumentLinkFromElement(JsonElement item)
    {
        if (!item.TryGetProperty("range", out var range)) return null;
        if (!range.TryGetProperty("start", out var start)) return null;
        if (!range.TryGetProperty("end", out var end)) return null;
        if (!start.TryGetProperty("line", out var startLineProp) || !start.TryGetProperty("character", out var startCharProp)) return null;
        if (!end.TryGetProperty("line", out var endLineProp) || !end.TryGetProperty("character", out var endCharProp)) return null;

        var target = item.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        var tooltip = item.TryGetProperty("tooltip", out var tt) && tt.ValueKind == JsonValueKind.String
            ? tt.GetString()
            : null;

        return new LspDocumentLink
        {
            StartLine = startLineProp.GetInt32(),
            StartCharacter = startCharProp.GetInt32(),
            EndLine = endLineProp.GetInt32(),
            EndCharacter = endCharProp.GetInt32(),
            Target = target,
            Tooltip = tooltip,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _fileWatcher.OnChanges -= OnFileChanges;
        _fileWatcher.Dispose();

        foreach (var tracker in _trackers.Values)
        {
            try { await tracker.CloseAllAsync(); } catch { /* best effort */ }
            tracker.Dispose();
        }
        _trackers.Clear();

        foreach (var conn in _connections.Values)
        {
            await conn.DisposeAsync();
        }
        _connections.Clear();

        // Clear pending edits and resultId state
        while (_pendingEdits.TryDequeue(out _)) { }
        _diagnosticsResultId.Clear();

        foreach (var sem in _serverLocks.Values) sem.Dispose();
        _serverLocks.Clear();
    }
}
