using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// A check report (<c>CheckReport</c>) as the text Keep and <c>fleet_mod_check</c> show, the same text the host's
/// <c>formatReport</c> (<c>mods/host/src/check/format.ts</c>) writes: the contract's "Its check report" for test-chips.
/// </summary>
public static class ModCheckText
{
    private const string Indent = "        ";

    public static string Format(JsonElement report)
    {
        var lines = Number(report, "lines");
        var hooks = Items(report, "hooks").Select(FormatHook).ToList();
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{Text(report, "name")} {Text(report, "version")} · {lines} {(lines == 1 ? "line" : "lines")}")
            .Append("\nhooks:  ").Append(hooks.Count == 0 ? "(none)" : string.Join("\n" + Indent, hooks))
            .Append("\ncalls:  ").Append(List(report, "calls"))
            .Append("\nstate:  ").Append(List(report, "state"))
            .Append("\npages:  ").Append(List(report, "pages"));
        foreach (var problem in Items(report, "errors"))
            text.Append('\n').Append(ProblemLine("error", problem));
        foreach (var problem in Items(report, "warnings"))
            text.Append('\n').Append(ProblemLine("warning", problem));
        return text.ToString();
    }

    /// <summary>One hook (<c>CheckReportHook</c>) as the <c>hooks:</c> line shows it: <c>ui.render "ToolUse" { props: … }</c>.</summary>
    public static string FormatHook(JsonElement hook)
    {
        var name = Text(hook, "event");
        if (!hook.TryGetProperty("matcher", out var matcher))
            return name;

        // A render matcher leads with its site, as the hooks: line in the contract does.
        if (name == "ui.render" && matcher.ValueKind == JsonValueKind.Object && !IsRegex(matcher)
            && matcher.TryGetProperty("component", out var component))
        {
            var rest = matcher.EnumerateObject().Where(p => p.Name != "component").ToList();
            return rest.Count == 0 ? $"{name} {Value(component)}" : $"{name} {Value(component)} {Object(rest)}";
        }

        return $"{name} {Value(matcher)}";
    }

    /// <summary>A matcher value as the host prints it: JSON, a RegExp as <c>/source/flags</c>, object keys bare when they can be.</summary>
    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object when IsRegex(value) => $"/{Text(value, "$regex")}/{Text(value, "flags")}",
        JsonValueKind.Object => Object([.. value.EnumerateObject()]),
        JsonValueKind.Array => $"[{string.Join(',', value.EnumerateArray().Select(Value))}]",
        JsonValueKind.String => Quote(value.GetString()!),
        _ => value.GetRawText(),
    };

    private static string Object(List<JsonProperty> properties)
        => properties.Count == 0
            ? "{}"
            : $"{{ {string.Join(", ", properties.Select(p => $"{(IsIdentifier(p.Name) ? p.Name : Quote(p.Name))}: {Value(p.Value)}"))} }}";

    private static bool IsRegex(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$regex", out _);

    /// <summary>JavaScript's <c>/^[A-Za-z_$][\w$]*$/</c>.</summary>
    private static bool IsIdentifier(string key)
        => key.Length > 0
           && (char.IsAsciiLetter(key[0]) || key[0] is '_' or '$')
           && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');

    /// <summary>A string as <c>JSON.stringify</c> writes it: only quotes, backslashes and control characters are escaped.</summary>
    private static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"),
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    private static string ProblemLine(string kind, JsonElement problem)
    {
        var at = problem.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number
            ? $"{line.GetRawText()}:{(problem.TryGetProperty("column", out var column) && column.ValueKind == JsonValueKind.Number ? column.GetRawText() : "1")}"
            : "-";
        return $"{kind.PadRight(8)}{at}  {Text(problem, "code")}  {Text(problem, "message")}";
    }

    private static string List(JsonElement report, string name)
    {
        var items = Items(report, name).Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText()).ToList();
        return items.Count == 0 ? "(none)" : string.Join(", ", items);
    }

    private static List<JsonElement> Items(JsonElement report, string name)
        => report.ValueKind == JsonValueKind.Object && report.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray()]
            : [];

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : "";

    private static long Number(JsonElement report, string name)
        => report.ValueKind == JsonValueKind.Object && report.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
