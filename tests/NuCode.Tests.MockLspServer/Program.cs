using System.Text;
using System.Text.Json;

// A minimal in-process mock LSP server.
// Reads LSP messages from stdin and responds with scripted JSON-RPC responses.
// Used by NuCode.Tests to test LspConnection without a real language server.
//
// Modes (pass as first arg):
//   (default)   — full capabilities, all Phase A + Phase B handlers
//   crash       — exits immediately after initialize (crash recovery test)
//   limited     — only hoverProvider + definitionProvider (capability gating test)

var mode = args.Length > 0 ? args[0] : "default";
var crashMode = mode == "crash";
var limitedMode = mode == "limited";

// File URI used in rename / applyEdit responses — taken from the first didOpen or defaulted
var openedFileUri = "file:///mock/file.ts";

while (true)
{
    var msg = await ReadMessageAsync(Console.OpenStandardInput());
    if (msg is null) break;

    if (!msg.Value.TryGetProperty("method", out var methodProp)) continue;
    var method = methodProp.GetString();

    msg.Value.TryGetProperty("id", out var idProp);
    var hasId = idProp.ValueKind == JsonValueKind.Number;
    var id = hasId ? idProp.GetInt32() : 0;

    msg.Value.TryGetProperty("params", out var paramsEl);

    switch (method)
    {
        case "initialize":
        {
            object capabilities;
            if (limitedMode)
            {
                capabilities = new
                {
                    hoverProvider = true,
                    definitionProvider = true,
                    textDocumentSync = new { openClose = true, change = 1 },
                };
            }
            else
            {
                capabilities = new
                {
                    hoverProvider = true,
                    definitionProvider = true,
                    referencesProvider = true,
                    implementationProvider = true,
                    documentSymbolProvider = true,
                    workspaceSymbolProvider = true,
                    callHierarchyProvider = true,
                    typeHierarchyProvider = true,
                    completionProvider = new { triggerCharacters = new[] { "." } },
                    codeActionProvider = new { resolveProvider = true },
                    documentFormattingProvider = true,
                    renameProvider = true,
                    signatureHelpProvider = new { triggerCharacters = new[] { "(" } },
                    diagnosticProvider = new { identifier = "mock", interFileDependencies = false, workspaceDiagnostics = false },
                    semanticTokensProvider = new
                    {
                        full = true,
                        legend = new
                        {
                            tokenTypes = new[] { "namespace", "type", "class", "function", "variable" },
                            tokenModifiers = new[] { "declaration", "definition", "readonly" },
                        },
                    },
                    inlayHintProvider = true,
                    codeLensProvider = new { resolveProvider = true },
                    foldingRangeProvider = true,
                    selectionRangeProvider = true,
                    typeDefinitionProvider = true,
                    declarationProvider = true,
                    documentHighlightProvider = true,
                    executeCommandProvider = new { commands = new[] { "test.command" } },
                    documentLinkProvider = new { resolveProvider = true },
                    textDocumentSync = new { openClose = true, change = 1 },
                };
            }

            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new { capabilities }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));

            if (crashMode) Environment.Exit(0);
            break;
        }
        case "initialized":
        {
            // After initialized, send client/registerCapability for file watching
            if (!limitedMode)
            {
                var nextId = 9000;
                var reg = new
                {
                    jsonrpc = "2.0",
                    id = nextId,
                    method = "client/registerCapability",
                    @params = new
                    {
                        registrations = new[]
                        {
                            new
                            {
                                id = "file-watcher-1",
                                method = "workspace/didChangeWatchedFiles",
                                registerOptions = new
                                {
                                    watchers = new[]
                                    {
                                        new { globPattern = "**/*.ts" }
                                    }
                                }
                            }
                        }
                    }
                };
                await WriteMessageAsync(JsonSerializer.Serialize(reg));

                // Send progress: create token, then begin/report/end
                var progressCreate = new
                {
                    jsonrpc = "2.0",
                    id = 9001,
                    method = "window/workDoneProgress/create",
                    @params = new { token = "mock-progress-1" },
                };
                await WriteMessageAsync(JsonSerializer.Serialize(progressCreate));

                var progressBegin = new
                {
                    jsonrpc = "2.0",
                    method = "$/progress",
                    @params = new
                    {
                        token = "mock-progress-1",
                        value = new { kind = "begin", title = "Indexing", percentage = 0 },
                    },
                };
                await WriteMessageAsync(JsonSerializer.Serialize(progressBegin));

                var progressReport = new
                {
                    jsonrpc = "2.0",
                    method = "$/progress",
                    @params = new
                    {
                        token = "mock-progress-1",
                        value = new { kind = "report", message = "50%", percentage = 50 },
                    },
                };
                await WriteMessageAsync(JsonSerializer.Serialize(progressReport));

                var progressEnd = new
                {
                    jsonrpc = "2.0",
                    method = "$/progress",
                    @params = new
                    {
                        token = "mock-progress-1",
                        value = new { kind = "end", message = "Done" },
                    },
                };
                await WriteMessageAsync(JsonSerializer.Serialize(progressEnd));
            }
            break;
        }
        case "shutdown":
        {
            var response = new { jsonrpc = "2.0", id, result = (object?)null };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/hover":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    contents = new { kind = "markdown", value = "mock hover" }
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/completion":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new { label = "console", kind = 7, detail = "Console class" },
                    new { label = "log", kind = 2, detail = "log method", insertText = "log($0)" },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/codeAction":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new { title = "Extract variable", kind = "refactor.extract" },
                    new { title = "Fix typo", kind = "quickfix", isPreferred = true },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "codeAction/resolve":
        {
            // Return resolved code action with inline edit
            var fileUri = openedFileUri;
            var resolved = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    title = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("title", out var t)
                        ? t.GetString() : "Fix typo",
                    kind = "quickfix",
                    edit = new
                    {
                        changes = new Dictionary<string, object[]>
                        {
                            [fileUri] = new object[]
                            {
                                new
                                {
                                    range = new
                                    {
                                        start = new { line = 0, character = 0 },
                                        end   = new { line = 0, character = 5 }
                                    },
                                    newText = "fixed"
                                }
                            }
                        }
                    }
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(resolved));
            break;
        }
        case "textDocument/formatting":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 0, character = 0 },
                            end   = new { line = 0, character = 5 }
                        },
                        newText = "const"
                    }
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/rename":
        {
            var fileUri = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("textDocument", out var td)
                          && td.TryGetProperty("uri", out var u) ? u.GetString() : openedFileUri;
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    changes = new Dictionary<string, object[]>
                    {
                        [fileUri!] = new object[]
                        {
                            new
                            {
                                range = new
                                {
                                    start = new { line = 0, character = 6 },
                                    end   = new { line = 0, character = 7 }
                                },
                                newText = "newName"
                            }
                        }
                    }
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/signatureHelp":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    signatures = new object[]
                    {
                        new
                        {
                            label = "log(message: string): void",
                            documentation = "Logs a message",
                            parameters = new object[]
                            {
                                new { label = "message: string", documentation = "The message" }
                            }
                        }
                    },
                    activeSignature = 0,
                    activeParameter = 0
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/diagnostic":
        {
            var fileUri = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("textDocument", out var dtd)
                          && dtd.TryGetProperty("uri", out var du) ? du.GetString() : openedFileUri;
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    kind = "full",
                    resultId = "mock-result-1",
                    items = new object[]
                    {
                        new
                        {
                            message = "mock pull diagnostic",
                            severity = 2, // Warning
                            source = "mock",
                            range = new
                            {
                                start = new { line = 1, character = 0 },
                                end   = new { line = 1, character = 10 }
                            }
                        }
                    }
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/definition":
        case "textDocument/references":
        case "textDocument/implementation":
        case "textDocument/documentSymbol":
        case "textDocument/prepareCallHierarchy":
        case "callHierarchy/incomingCalls":
        case "callHierarchy/outgoingCalls":
        case "workspace/symbol":
        {
            var response = new { jsonrpc = "2.0", id, result = Array.Empty<object>() };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/semanticTokens/full":
        {
            // Return one token: line 0, char 0, length 5, tokenType 2 (class), modifiers 0
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    data = new[] { 0, 0, 5, 2, 0 },
                    resultId = "mock-1",
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/inlayHint":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        position = new { line = 0, character = 5 },
                        label = ": number",
                        kind = 1,
                        paddingLeft = true,
                        paddingRight = false,
                    },
                    new
                    {
                        position = new { line = 1, character = 10 },
                        label = "param:",
                        kind = 2,
                        paddingLeft = false,
                        paddingRight = true,
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/codeLens":
        {
            // Return one unresolved code lens
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 0, character = 0 },
                            end = new { line = 0, character = 10 },
                        },
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "codeLens/resolve":
        {
            // Resolve with a command
            var range = paramsEl.TryGetProperty("range", out var r) ? r : default;
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    range = new
                    {
                        start = new { line = 0, character = 0 },
                        end = new { line = 0, character = 10 },
                    },
                    command = new
                    {
                        title = "1 reference",
                        command = "editor.action.showReferences",
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/foldingRange":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        startLine = 0,
                        startCharacter = 0,
                        endLine = 5,
                        endCharacter = 1,
                        kind = "region",
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/selectionRange":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 0, character = 5 },
                            end = new { line = 0, character = 10 },
                        },
                        parent = new
                        {
                            range = new
                            {
                                start = new { line = 0, character = 0 },
                                end = new { line = 0, character = 20 },
                            },
                        },
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/typeDefinition":
        {
            var fileUri = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("textDocument", out var ttd)
                          && ttd.TryGetProperty("uri", out var tu) ? tu.GetString() : openedFileUri;
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        uri = fileUri,
                        range = new
                        {
                            start = new { line = 10, character = 0 },
                            end = new { line = 10, character = 8 },
                        },
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/declaration":
        {
            var fileUri = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("textDocument", out var dtd2)
                          && dtd2.TryGetProperty("uri", out var du2) ? du2.GetString() : openedFileUri;
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        uri = fileUri,
                        range = new
                        {
                            start = new { line = 5, character = 4 },
                            end = new { line = 5, character = 12 },
                        },
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/prepareTypeHierarchy":
        {
            var fileUri = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("textDocument", out var pthTd)
                          && pthTd.TryGetProperty("uri", out var pthUri) ? pthUri.GetString() : openedFileUri;
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        name = "MyClass",
                        kind = 5,
                        uri = fileUri,
                        range = new
                        {
                            start = new { line = 0, character = 0 },
                            end = new { line = 0, character = 7 },
                        },
                        selectionRange = new
                        {
                            start = new { line = 0, character = 0 },
                            end = new { line = 0, character = 7 },
                        },
                        detail = "MyNamespace",
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "typeHierarchy/supertypes":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        name = "BaseClass",
                        kind = 5,
                        uri = openedFileUri,
                        range = new
                        {
                            start = new { line = 10, character = 0 },
                            end = new { line = 10, character = 9 },
                        },
                        selectionRange = new
                        {
                            start = new { line = 10, character = 0 },
                            end = new { line = 10, character = 9 },
                        },
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "typeHierarchy/subtypes":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        name = "DerivedClass",
                        kind = 5,
                        uri = openedFileUri,
                        range = new
                        {
                            start = new { line = 20, character = 0 },
                            end = new { line = 20, character = 12 },
                        },
                        selectionRange = new
                        {
                            start = new { line = 20, character = 0 },
                            end = new { line = 20, character = 12 },
                        },
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/documentHighlight":
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new object[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 0, character = 6 },
                            end = new { line = 0, character = 9 },
                        },
                        kind = 2, // Read
                    },
                    new
                    {
                        range = new
                        {
                            start = new { line = 3, character = 0 },
                            end = new { line = 3, character = 3 },
                        },
                        kind = 3, // Write
                    },
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "workspace/executeCommand":
        {
            var cmdName = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("command", out var cmdProp)
                ? cmdProp.GetString() : "unknown";
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = $"Command executed: {cmdName}",
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/documentLink":
        {
            if (limitedMode)
            {
                var response = new { jsonrpc = "2.0", id, result = Array.Empty<object>() };
                await WriteMessageAsync(JsonSerializer.Serialize(response));
            }
            else
            {
                var response = new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new object[]
                    {
                        new
                        {
                            range = new
                            {
                                start = new { line = 0, character = 0 },
                                end = new { line = 0, character = 10 },
                            },
                            target = "https://example.com",
                            tooltip = "Example",
                        },
                        new
                        {
                            range = new
                            {
                                start = new { line = 1, character = 0 },
                                end = new { line = 1, character = 15 },
                            },
                            data = new { linkIndex = 1 },
                        },
                    }
                };
                await WriteMessageAsync(JsonSerializer.Serialize(response));
            }
            break;
        }
        case "documentLink/resolve":
        {
            // Return resolved link with target filled in
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    range = new
                    {
                        start = new { line = 1, character = 0 },
                        end = new { line = 1, character = 15 },
                    },
                    target = "https://resolved.com",
                }
            };
            await WriteMessageAsync(JsonSerializer.Serialize(response));
            break;
        }
        case "textDocument/didOpen":
        {
            // Track the opened URI for use in responses
            if (paramsEl.ValueKind == JsonValueKind.Object &&
                paramsEl.TryGetProperty("textDocument", out var otd) &&
                otd.TryGetProperty("uri", out var ou))
            {
                openedFileUri = ou.GetString() ?? openedFileUri;
            }
            break;
        }
        case "textDocument/didChange":
        case "textDocument/didClose":
        case "workspace/didChangeWatchedFiles":
        case "$/cancelRequest":
            // Notifications — no response needed
            break;
        case "exit":
            Environment.Exit(0);
            break;
        default:
            if (hasId)
            {
                // Unknown request — respond with method not found
                var err = new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = $"Method not found: {method}" }
                };
                await WriteMessageAsync(JsonSerializer.Serialize(err));
            }
            break;
    }
}

static async Task<JsonElement?> ReadMessageAsync(Stream stream)
{
    var headerBuffer = new List<byte>();
    var state = 0; // 0=normal,1=\r,2=\r\n,3=\r\n\r

    while (true)
    {
        var b = stream.ReadByte();
        if (b < 0) return null;

        headerBuffer.Add((byte)b);

        if (b == '\r' && state == 0) state = 1;
        else if (b == '\n' && state == 1) state = 2;
        else if (b == '\r' && state == 2) state = 3;
        else if (b == '\n' && state == 3) break;
        else state = 0;
    }

    var header = Encoding.ASCII.GetString(headerBuffer.ToArray());
    var clMatch = System.Text.RegularExpressions.Regex.Match(header, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!clMatch.Success) return null;

    var contentLength = int.Parse(clMatch.Groups[1].Value);
    var body = new byte[contentLength];
    var totalRead = 0;
    while (totalRead < contentLength)
    {
        var read = await stream.ReadAsync(body.AsMemory(totalRead, contentLength - totalRead));
        if (read == 0) return null;
        totalRead += read;
    }

    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.Clone();
}

static async Task WriteMessageAsync(string json)
{
    var content = Encoding.UTF8.GetBytes(json);
    var header = $"Content-Length: {content.Length}\r\n\r\n";
    var headerBytes = Encoding.ASCII.GetBytes(header);
    var stdout = Console.OpenStandardOutput();
    await stdout.WriteAsync(headerBytes);
    await stdout.WriteAsync(content);
    await stdout.FlushAsync();
}
