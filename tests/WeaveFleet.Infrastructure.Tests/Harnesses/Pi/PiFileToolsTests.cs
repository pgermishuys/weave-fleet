using System.Text.Json;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

/// <summary>
/// Pi's write and edit tools as Fleet's file events: what tracks a plan's progress (<c>files.written</c>, with what the
/// call did) and what reloads open files and the Changes panel (<c>file.watcher.updated</c>). Pi says neither itself.
/// </summary>
public sealed class PiFileToolsTests
{
    private const string SessionId = "session-1";
    private static readonly string Work = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pi-work"));

    [Fact]
    public void a_finished_write_says_the_file_it_wrote_and_its_whole_content()
    {
        var events = Run("write", """{"path":"PLAN.md","content":"- [ ] One\n"}""");

        var (payload, changed) = FileEvents(events);
        payload.Paths.ShouldBe([Path.Combine(Work, "PLAN.md")]);
        payload.MessageId.ShouldBe("pi-session-1-assistant-1000");
        var change = payload.Changes.ShouldNotBeNull().ShouldHaveSingleItem();
        (change.Path, change.Content).ShouldBe((Path.Combine(Work, "PLAN.md"), "- [ ] One\n"));
        changed.GetProperty("file").GetString().ShouldBe(Path.Combine(Work, "PLAN.md"));
        changed.GetProperty("event").GetString().ShouldBe("change");
    }

    [Fact]
    public void a_finished_edit_says_each_of_its_replacements_in_order()
    {
        var events = Run("edit", """{"path":"/plans/PLAN.md","edits":[{"oldText":"- [ ] One","newText":"- [x] One"},{"oldText":"- [ ] Two","newText":"- [x] Two"}]}""");

        var changes = FileEvents(events).Payload.Changes.ShouldNotBeNull();
        changes.Select(c => (c.Path, c.OldString, c.NewString, c.ReplaceAll, c.Content)).ShouldBe(
        [
            ("/plans/PLAN.md", "- [ ] One", "- [x] One", false, null),
            ("/plans/PLAN.md", "- [ ] Two", "- [x] Two", false, null),
        ]);
    }

    [Fact]
    public void an_edit_whose_edits_came_as_a_json_string_still_says_them()
    {
        var events = Run("edit", """{"path":"PLAN.md","edits":"[{\"oldText\":\"- [ ] One\",\"newText\":\"- [x] One\"}]"}""");

        var change = FileEvents(events).Payload.Changes.ShouldNotBeNull().ShouldHaveSingleItem();
        (change.OldString, change.NewString).ShouldBe(("- [ ] One", "- [x] One"));
    }

    [Fact]
    public void an_older_models_one_replacement_comes_after_any_in_edits_as_pi_makes_it()
    {
        var events = Run("edit", """{"path":"PLAN.md","edits":[{"oldText":"a","newText":"b"}],"oldText":"c","newText":"d"}""");

        FileEvents(events).Payload.Changes.ShouldNotBeNull().Select(c => (c.OldString, c.NewString)).ShouldBe([("a", "b"), ("c", "d")]);
    }

    [Fact]
    public void an_edit_with_a_replacement_fleet_cant_apply_says_none_so_fleet_reads_the_file()
    {
        var events = Run("edit", """{"path":"PLAN.md","edits":[{"oldText":"a","newText":"b"},{"oldText":"","newText":"c"}]}""");

        var (payload, _) = FileEvents(events);
        payload.Paths.ShouldBe([Path.Combine(Work, "PLAN.md")]);
        payload.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void a_path_is_resolved_as_pi_resolves_it()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        PiFileTools.WrittenFile("write", Json("""{"path":"@docs/PLAN.md"}"""), Work).ShouldBe(Path.Combine(Work, "docs", "PLAN.md"));
        PiFileTools.WrittenFile("write", Json("""{"path":"~/PLAN.md"}"""), Work).ShouldBe(Path.Combine(home, "PLAN.md"));
        PiFileTools.WrittenFile("edit", Json("""{"path":"./a/../PLAN.md"}"""), Work).ShouldBe(Path.Combine(Work, "PLAN.md"));
        PiFileTools.WrittenFile("read", Json("""{"path":"PLAN.md"}"""), Work).ShouldBeNull();
        PiFileTools.WrittenFile("write", Json("""{"content":"x"}"""), Work).ShouldBeNull();
    }

    [Fact]
    public void a_failed_write_or_another_tool_says_no_file_was_written()
    {
        Run("write", """{"path":"PLAN.md","content":"x"}""", isError: true).ShouldNotContain(e => e.Type == EventTypes.FilesWritten);
        Run("read", """{"path":"PLAN.md"}""").ShouldNotContain(e => e.Type == EventTypes.FilesWritten);
        Run("bash", """{"command":"echo hi > PLAN.md"}""").ShouldNotContain(e => e.Type == EventTypes.FilesWritten);
    }

    private static List<HarnessEvent> Run(string tool, string args, bool isError = false)
    {
        var mapper = new PiMapper(SessionId, null, Work);
        mapper.Map(new PiMessageStartEvent { Message = new PiMessage { Role = "assistant", Content = [], Timestamp = 1000 } });
        mapper.Map(new PiToolExecutionStartEvent { ToolCallId = "call-1", ToolName = tool, Args = Json(args) });
        return [.. mapper.Map(new PiToolExecutionEndEvent { ToolCallId = "call-1", ToolName = tool, Result = Done(), IsError = isError })];
    }

    private static PiToolResult Done() => new() { Content = [new PiTextContent { Text = "ok" }] };

    private static (FilesWrittenPayload Payload, JsonElement Changed) FileEvents(IEnumerable<HarnessEvent> events)
    {
        var list = events.ToList();
        var written = list.Where(e => e.Type == EventTypes.FilesWritten).ShouldHaveSingleItem();
        var changed = list.Where(e => e.Type == EventTypes.FileWatcherUpdated).ShouldHaveSingleItem();
        return (
            written.Payload.ShouldNotBeNull().Deserialize(InfrastructureJsonContext.Default.FilesWrittenPayload).ShouldNotBeNull(),
            changed.Payload.ShouldNotBeNull());
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
