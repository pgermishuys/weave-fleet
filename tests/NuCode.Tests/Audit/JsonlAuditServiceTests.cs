using System.Text.Json;
using NuCode.Audit;

namespace NuCode;

public sealed class JsonlAuditServiceTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public JsonlAuditServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        await ValueTask.CompletedTask;
    }

    [Fact]
    public static void build_file_path_returns_expected_path()
    {
        var path = JsonlAuditService.BuildFilePath("/workspace", "sess-123");
        path.ShouldBe(Path.Combine("/workspace", ".nucode", "audit", "sess-123.jsonl"));
    }

    [Fact]
    public async Task records_tool_invocation_writes_jsonl_line()
    {
        await using var svc = new JsonlAuditService(_tempDir);

        var entry = new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: "sess-abc",
            ToolName: "bash",
            ArgsSummary: "command=ls",
            Status: "completed",
            DurationMs: 42,
            AgentId: null,
            Detail: null);

        await svc.RecordToolInvocationAsync(entry);
        await svc.DisposeAsync();

        var filePath = JsonlAuditService.BuildFilePath(_tempDir, "sess-abc");
        File.Exists(filePath).ShouldBeTrue();
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.ShouldHaveSingleItem();
        var doc = JsonDocument.Parse(lines[0]);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe("tool");
        doc.RootElement.GetProperty("toolName").GetString().ShouldBe("bash");
        doc.RootElement.GetProperty("status").GetString().ShouldBe("completed");
        doc.RootElement.GetProperty("durationMs").GetInt64().ShouldBe(42);
    }

    [Fact]
    public async Task records_permission_decision_writes_jsonl_line()
    {
        await using var svc = new JsonlAuditService(_tempDir);

        var entry = new AuditPermissionEntry(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: "sess-def",
            Permission: "bash",
            Patterns: ["*.sh"],
            Decision: "allow");

        await svc.RecordPermissionDecisionAsync(entry);
        await svc.DisposeAsync();

        var filePath = JsonlAuditService.BuildFilePath(_tempDir, "sess-def");
        File.Exists(filePath).ShouldBeTrue();
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.ShouldHaveSingleItem();
        var doc = JsonDocument.Parse(lines[0]);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe("permission");
        doc.RootElement.GetProperty("permission").GetString().ShouldBe("bash");
        doc.RootElement.GetProperty("decision").GetString().ShouldBe("allow");
    }

    [Fact]
    public async Task writes_multiple_sessions_to_separate_files()
    {
        await using var svc = new JsonlAuditService(_tempDir);

        await svc.RecordToolInvocationAsync(new AuditEntry(
            DateTimeOffset.UtcNow, "sess-1", "read", null, "completed", 10, null, null));
        await svc.RecordToolInvocationAsync(new AuditEntry(
            DateTimeOffset.UtcNow, "sess-2", "write", null, "error", 5, null, "fail"));
        await svc.DisposeAsync();

        File.Exists(JsonlAuditService.BuildFilePath(_tempDir, "sess-1")).ShouldBeTrue();
        File.Exists(JsonlAuditService.BuildFilePath(_tempDir, "sess-2")).ShouldBeTrue();
    }

    [Fact]
    public async Task appends_to_existing_file()
    {
        await using (var svc = new JsonlAuditService(_tempDir))
        {
            await svc.RecordToolInvocationAsync(new AuditEntry(
                DateTimeOffset.UtcNow, "sess-append", "bash", null, "completed", 1, null, null));
        }

        await using (var svc2 = new JsonlAuditService(_tempDir))
        {
            await svc2.RecordToolInvocationAsync(new AuditEntry(
                DateTimeOffset.UtcNow, "sess-append", "read", null, "completed", 2, null, null));
        }

        var lines = await File.ReadAllLinesAsync(
            JsonlAuditService.BuildFilePath(_tempDir, "sess-append"));
        lines.Length.ShouldBe(2);
    }
}
