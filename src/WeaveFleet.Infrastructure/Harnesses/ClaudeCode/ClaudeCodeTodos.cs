using System.Globalization;
using System.Text.Json;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// The agent's todo list, as Fleet reads it from Claude Code's tool calls. Claude Code 2.1.290 keeps it with
/// <c>TaskCreate</c> (one item; its id comes back in the result), <c>TaskUpdate</c> (an item's status or subject; status
/// <c>deleted</c> takes it off) and <c>TaskList</c> (the whole list, which Fleet takes as it is). Older CLIs send the whole
/// list with each <c>TodoWrite</c>. Kept for as long as the session's object lives: after Fleet restarts, an update to an
/// item Fleet didn't see made changes nothing until the next <c>TaskList</c>.
/// </summary>
internal sealed class ClaudeCodeTodos
{
    private readonly SortedDictionary<string, TodoEntry> _items = new(Comparer<string>.Create(CompareIds));

    /// <summary>
    /// Applies a finished call of Claude Code's: its name, its input and its <c>tool_use_result</c>. Returns the whole
    /// list when the call changed it, otherwise null.
    /// </summary>
    internal IReadOnlyList<TodoEntry>? Apply(string? claudeName, JsonElement input, JsonElement? result)
    {
        switch (claudeName)
        {
            case "TodoWrite" when Array(input, "todos") is { } todos:
                Replace(todos, idName: null, contentName: "content");
                return Items();

            case "TaskList" when result is { } listed && Array(listed, "tasks") is { } tasks:
                Replace(tasks, idName: "id", contentName: "subject");
                return Items();

            case "TaskCreate" when result is { ValueKind: JsonValueKind.Object } created
                && created.TryGetProperty("task", out var task) && Str(task, "id") is { } id
                && (Str(task, "subject") ?? Str(input, "subject")) is { Length: > 0 } subject:
                _items[id] = new TodoEntry { Content = subject, Status = TodoStatuses.Pending };
                return Items();

            case "TaskUpdate" when Str(input, "taskId") is { } taskId && _items.TryGetValue(taskId, out var item):
                if (Str(input, "status") == "deleted")
                {
                    _items.Remove(taskId);
                    return Items();
                }

                var updated = item with
                {
                    Content = Str(input, "subject") is { Length: > 0 } renamed ? renamed : item.Content,
                    Status = Str(input, "status") is { } status ? TodoStatuses.Normalize(status) : item.Status,
                };
                if (updated == item)
                    return null;
                _items[taskId] = updated;
                return Items();

            default:
                return null;
        }
    }

    private void Replace(JsonElement list, string? idName, string contentName)
    {
        _items.Clear();
        var index = 0;
        foreach (var entry in list.EnumerateArray())
        {
            index++;
            if (entry.ValueKind != JsonValueKind.Object || Str(entry, contentName) is not { Length: > 0 } content)
                continue;
            var id = (idName is null ? null : Str(entry, idName)) ?? index.ToString(CultureInfo.InvariantCulture);
            _items[id] = new TodoEntry { Content = content, Status = TodoStatuses.Normalize(Str(entry, "status")), Priority = Str(entry, "priority") };
        }
    }

    private List<TodoEntry> Items() => [.. _items.Values];

    /// <summary>Claude Code's ids count up from 1: in number order, so item 10 comes after item 9.</summary>
    private static int CompareIds(string a, string b)
        => long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var x) && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            ? x.CompareTo(y)
            : string.CompareOrdinal(a, b);

    private static JsonElement? Array(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

    private static string? Str(JsonElement element, string name) => PermissionEvents.String(element, name);
}
