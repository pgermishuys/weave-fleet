using System.Text.Json;
using Shouldly;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>The agent's todo list as Fleet keeps it from Claude Code's task calls (<see cref="ClaudeCodeTodos"/>).</summary>
public sealed class ClaudeCodeTodosTests
{
    private readonly ClaudeCodeTodos _todos = new();

    [Fact]
    public void TodoWrite_replaces_the_list_with_the_one_it_sends()
    {
        Create("1", "Old");

        var list = _todos.Apply("TodoWrite", Json("""
            {"todos":[{"content":"Read the code","status":"completed","activeForm":"Reading"},{"content":"Fix it","status":"in_progress"},{"content":"","status":"pending"}]}
            """), Json("{}"));

        Shown(list).ShouldBe(["Read the code: completed", "Fix it: in_progress"]);
    }

    [Fact]
    public void Items_keep_the_order_of_their_ids_as_numbers()
    {
        for (var i = 1; i <= 10; i++)
            Create(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Item {i}");

        var list = _todos.Apply("TaskUpdate", Json("""{"taskId":"9","status":"completed"}"""), Json("""{"success":true}"""))!;

        list.Select(i => i.Content).ShouldBe(Enumerable.Range(1, 10).Select(i => $"Item {i}"));
        list[8].Status.ShouldBe(TodoStatuses.Completed);
    }

    [Fact]
    public void An_update_to_an_item_Fleet_never_saw_or_that_changes_nothing_reports_nothing()
    {
        Create("1", "Write a.txt");

        _todos.Apply("TaskUpdate", Json("""{"taskId":"7","status":"completed"}"""), Json("""{"success":true}""")).ShouldBeNull();
        _todos.Apply("TaskUpdate", Json("""{"taskId":"1","status":"pending"}"""), Json("""{"success":true}""")).ShouldBeNull();
        _todos.Apply("TaskUpdate", Json("""{"taskId":"1","addBlocks":["2"]}"""), Json("""{"success":true}""")).ShouldBeNull();
    }

    [Fact]
    public void TaskList_brings_back_items_Fleet_lost_track_of()
    {
        // As after Fleet restarted: Claude Code still has the list, Fleet doesn't.
        var list = _todos.Apply("TaskList", Json("{}"), Json("""
            {"tasks":[{"id":"2","subject":"Second","status":"pending","blockedBy":[]},{"id":"1","subject":"First","status":"completed","blockedBy":[]}]}
            """));

        Shown(list).ShouldBe(["First: completed", "Second: pending"]);
        Shown(_todos.Apply("TaskUpdate", Json("""{"taskId":"2","status":"in_progress"}"""), Json("""{"success":true}""")))
            .ShouldBe(["First: completed", "Second: in_progress"]);
    }

    [Fact]
    public void Other_tools_and_a_create_without_its_id_change_nothing()
    {
        _todos.Apply("Bash", Json("""{"command":"ls"}"""), Json("""{"stdout":""}""")).ShouldBeNull();
        _todos.Apply("TaskCreate", Json("""{"subject":"No id","description":"d"}"""), null).ShouldBeNull();
        _todos.Apply("TaskGet", Json("""{"taskId":"1"}"""), Json("""{"task":{"id":"1","subject":"x"}}""")).ShouldBeNull();
    }

    private void Create(string id, string subject)
        => _todos.Apply("TaskCreate", Json($$"""{"subject":"{{subject}}","description":"d"}"""), Json($$$"""{"task":{"id":"{{{id}}}","subject":"{{{subject}}}"}}"""));

    private static List<string> Shown(IReadOnlyList<TodoEntry>? list) => list!.Select(i => $"{i.Content}: {i.Status}").ToList();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
