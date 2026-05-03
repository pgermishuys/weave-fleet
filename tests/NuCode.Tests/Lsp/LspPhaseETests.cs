using System.Reflection;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Lsp;
using NuCode.Tools;

namespace NuCode.Lsp;

/// <summary>Integration tests for LSP Phase E features (type hierarchy).</summary>
public sealed class LspPhaseETests : IAsyncLifetime
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

    public LspPhaseETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_PhaseETests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── ILspService direct tests ────────────────────────────────────────────

    [Fact]
    public async Task prepare_type_hierarchy_returns_items()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.PrepareTypeHierarchyAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("MyClass");
        result[0].Kind.ShouldBe("Class");
        result[0].Detail.ShouldBe("MyNamespace");
    }

    [Fact]
    public async Task supertypes_returns_parent_types()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass extends BaseClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.SupertypesAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("BaseClass");
        result[0].Kind.ShouldBe("Class");
    }

    [Fact]
    public async Task subtypes_returns_child_types()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");

        var result = await _manager.SubtypesAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("DerivedClass");
        result[0].Kind.ShouldBe("Class");
    }

    [Fact]
    public async Task prepare_type_hierarchy_returns_empty_when_unsupported()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.PrepareTypeHierarchyAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task supertypes_returns_empty_when_unsupported()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.SupertypesAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task subtypes_returns_empty_when_unsupported()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts", mode: "limited");

        var result = await _manager.SubtypesAsync(testFile, 0, 6, CancellationToken.None);

        result.Count.ShouldBe(0);
    }

    // ── LspTool tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task prepare_type_hierarchy_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "prepareTypeHierarchy",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 6,
        });

        result.ShouldContain("MyClass");
        result.ShouldContain("Type hierarchy item");
    }

    [Fact]
    public async Task supertypes_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass extends BaseClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "supertypes",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 6,
        });

        result.ShouldContain("BaseClass");
        result.ShouldContain("supertype");
    }

    [Fact]
    public async Task subtypes_via_tool()
    {
        if (!File.Exists(MockServerExe)) throw new Exception($"{Xunit.v3.DynamicSkipToken.Value}Mock server not built");

        var testFile = Path.Combine(_tempDir, "test.ts");
        await File.WriteAllTextAsync(testFile, "class MyClass {}");

        _manager = CreateManager("typescript", MockServerExe, ".ts");
        var tool = new LspTool(_manager);
        var fn = tool.ToAIFunction();

        var result = await Invoke(fn, new()
        {
            ["operation"] = "subtypes",
            ["filePath"] = testFile,
            ["line"] = 0,
            ["character"] = 6,
        });

        result.ShouldContain("DerivedClass");
        result.ShouldContain("subtype");
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
