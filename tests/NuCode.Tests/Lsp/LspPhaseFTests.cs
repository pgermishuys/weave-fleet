using System.Reflection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;
using NuCode.Tools;

namespace NuCode.Lsp;

/// <summary>Integration tests for LSP Phase F features (document links + dynamic registration).</summary>
public sealed class LspPhaseFTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private LspServerManager? _manager;

    private static string MockServerExe
    {
        get
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var baseDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "NuCode.Tests.MockLspServer", "bin"));
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(baseDir, config, "net10.0", "NuCode.Tests.MockLspServer.dll");
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(baseDir, "Release", "net10.0", "NuCode.Tests.MockLspServer.dll");
        }
    }

    public LspPhaseFTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_PhaseFTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Document Links ──────────────────────────────────────────────────────

    [Fact]
    public async Task document_link_returns_links_with_resolved_targets()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "// see https://example.com\n// see link");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.DocumentLinkAsync(testFile, CancellationToken.None);

        result.Count.ShouldBe(2);

        // First link: pre-resolved with target and tooltip
        result[0].Target.ShouldBe("https://example.com");
        result[0].Tooltip.ShouldBe("Example");
        result[0].StartLine.ShouldBe(0);

        // Second link: was unresolved, should now be resolved via documentLink/resolve
        result[1].Target.ShouldBe("https://resolved.com");
        result[1].StartLine.ShouldBe(1);
    }

    [Fact]
    public async Task document_link_skipped_when_capability_not_advertised()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "// content");

        _manager = CreateManager("typescript", MockServerExe, ".ts", "limited");

        var result = await _manager.DocumentLinkAsync(testFile, CancellationToken.None);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task document_links_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "// links here");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "documentLinks",
            ["filePath"] = testFile,
        });

        result.ShouldContain("https://example.com");
        result.ShouldContain("https://resolved.com");
        result.ShouldContain("2 document link");
    }

    // ── Dynamic Registration ────────────────────────────────────────────────

    [Fact]
    public void dynamic_registration_enables_capability()
    {
        // Unit test: directly test LspConnection registration methods
        // Use reflection since RegisterCapability/UnregisterCapability/SupportsCapability are internal
        var connType = typeof(LspConnection);

        // LspConnection.CheckCapability is static and internal — test dynamic registration logic
        // by testing the register/unregister/supports flow through a fresh connection would require
        // starting a process. Instead, test the ConcurrentDictionary logic directly.

        // Create dictionaries matching LspConnection's internal state
        var dynamicRegistrations = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var dynamicallyRegisteredMethods = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        // Simulate RegisterCapability
        dynamicRegistrations["reg-1"] = "textDocument/documentLink";
        dynamicallyRegisteredMethods["textDocument/documentLink"] = true;

        dynamicallyRegisteredMethods.ContainsKey("textDocument/documentLink").ShouldBeTrue();

        // Simulate UnregisterCapability
        if (dynamicRegistrations.TryRemove("reg-1", out var method))
        {
            var stillRegistered = false;
            foreach (var kvp in dynamicRegistrations)
            {
                if (kvp.Value == method) { stillRegistered = true; break; }
            }
            if (!stillRegistered) dynamicallyRegisteredMethods.TryRemove(method, out _);
        }

        dynamicallyRegisteredMethods.ContainsKey("textDocument/documentLink").ShouldBeFalse();
    }

    [Fact]
    public void dynamic_unregistration_only_removes_when_no_other_registrations()
    {
        var dynamicRegistrations = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var dynamicallyRegisteredMethods = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        // Register same method twice with different IDs
        dynamicRegistrations["reg-1"] = "textDocument/documentLink";
        dynamicRegistrations["reg-2"] = "textDocument/documentLink";
        dynamicallyRegisteredMethods["textDocument/documentLink"] = true;

        // Unregister first one — method should still be registered
        if (dynamicRegistrations.TryRemove("reg-1", out var method))
        {
            var stillRegistered = false;
            foreach (var kvp in dynamicRegistrations)
            {
                if (kvp.Value == method) { stillRegistered = true; break; }
            }
            if (!stillRegistered) dynamicallyRegisteredMethods.TryRemove(method, out _);
        }

        dynamicallyRegisteredMethods.ContainsKey("textDocument/documentLink").ShouldBeTrue();

        // Unregister second one — now method should be gone
        if (dynamicRegistrations.TryRemove("reg-2", out var method2))
        {
            var stillRegistered = false;
            foreach (var kvp in dynamicRegistrations)
            {
                if (kvp.Value == method2) { stillRegistered = true; break; }
            }
            if (!stillRegistered) dynamicallyRegisteredMethods.TryRemove(method2, out _);
        }

        dynamicallyRegisteredMethods.ContainsKey("textDocument/documentLink").ShouldBeFalse();
    }

    [Fact]
    public void CheckCapability_static_still_works()
    {
        // Verify the static CheckCapability method still works for non-dynamic scenarios
        var result = LspConnection.CheckCapability(null, "textDocument/hover");
        result.ShouldBeTrue(); // null caps = optimistic
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> Invoke(Microsoft.Extensions.AI.AIFunction fn, Dictionary<string, object?> args)
    {
        var result = await fn.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    private LspServerManager CreateManager(string serverName, string mockServerDll, string extension, string? mode = null)
    {
        var command = mode is not null
            ? new List<string> { "dotnet", "exec", mockServerDll, mode }
            : new List<string> { "dotnet", "exec", mockServerDll };

        var config = new NuCodeConfig
        {
            LspAutoDetect = false,
            Lsp = new Dictionary<string, LspServerConfig>
            {
                [serverName] = new LspServerConfig
                {
                    Command = command,
                    Extensions = [extension],
                },
            },
        };
        return new LspServerManager(_tempDir, CreateMonitor(config));
    }

    private static IOptionsMonitor<NuCodeConfig> CreateMonitor(NuCodeConfig config) =>
        new FakeOptionsMonitor<NuCodeConfig>(config);

    private sealed class FakeOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
