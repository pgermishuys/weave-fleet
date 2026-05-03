using System.Text.Json;

namespace NuCode.Lsp;

/// <summary>Unit tests for LspConnection capability checking.</summary>
public sealed class LspCapabilityTests
{
    private static JsonElement ParseCaps(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void check_capability_returns_true_when_bool_true()
    {
        var caps = ParseCaps("""{ "hoverProvider": true }""");
        LspConnection.CheckCapability(caps, "textDocument/hover").ShouldBeTrue();
    }

    [Fact]
    public void check_capability_returns_false_when_bool_false()
    {
        var caps = ParseCaps("""{ "hoverProvider": false }""");
        LspConnection.CheckCapability(caps, "textDocument/hover").ShouldBeFalse();
    }

    [Fact]
    public void check_capability_returns_true_when_capability_is_object()
    {
        var caps = ParseCaps("""{ "completionProvider": { "triggerCharacters": ["."] } }""");
        LspConnection.CheckCapability(caps, "textDocument/completion").ShouldBeTrue();
    }

    [Fact]
    public void check_capability_returns_false_when_property_missing()
    {
        var caps = ParseCaps("""{ "hoverProvider": true }""");
        LspConnection.CheckCapability(caps, "textDocument/rename").ShouldBeFalse();
    }

    [Fact]
    public void check_capability_returns_true_when_capabilities_null()
    {
        // No ServerCapabilities — optimistic
        LspConnection.CheckCapability(null, "textDocument/hover").ShouldBeTrue();
    }

    [Fact]
    public void check_capability_returns_true_for_unmapped_method()
    {
        var caps = ParseCaps("""{ "hoverProvider": false }""");
        LspConnection.CheckCapability(caps, "textDocument/unknownMethod").ShouldBeTrue();
    }

    [Fact]
    public void check_capability_correctly_maps_all_known_methods()
    {
        var caps = ParseCaps("""
        {
            "hoverProvider": true,
            "definitionProvider": true,
            "referencesProvider": false,
            "completionProvider": {},
            "codeActionProvider": false,
            "documentFormattingProvider": true,
            "renameProvider": false,
            "callHierarchyProvider": true,
            "workspaceSymbolProvider": false,
            "diagnosticProvider": {}
        }
        """);

        LspConnection.CheckCapability(caps, "textDocument/hover").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "textDocument/definition").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "textDocument/references").ShouldBeFalse();
        LspConnection.CheckCapability(caps, "textDocument/completion").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "textDocument/codeAction").ShouldBeFalse();
        LspConnection.CheckCapability(caps, "textDocument/formatting").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "textDocument/rename").ShouldBeFalse();
        LspConnection.CheckCapability(caps, "textDocument/prepareCallHierarchy").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "callHierarchy/incomingCalls").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "callHierarchy/outgoingCalls").ShouldBeTrue();
        LspConnection.CheckCapability(caps, "workspace/symbol").ShouldBeFalse();
        LspConnection.CheckCapability(caps, "textDocument/diagnostic").ShouldBeTrue();
    }
}
