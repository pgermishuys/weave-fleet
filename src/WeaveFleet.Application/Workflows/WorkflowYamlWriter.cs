using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Workflows;

/// <summary>
/// Writes a workflow file in Fleet's layout, the built-in's: the keys in the order the docs list them, a prompt as a
/// literal block, a blank line between steps. Written by hand, like <see cref="WorkflowYaml"/> reads by hand, so
/// nothing is found by reflection. Whatever it writes reads back as the same workflow: a scalar that wouldn't is
/// quoted.
/// </summary>
public static partial class WorkflowYamlWriter
{
    /// <summary>A description longer than this is folded over lines, like the built-in's.</summary>
    private const int FoldWidth = 100;

    public static string Write(WorkflowDefinition workflow)
    {
        var yaml = new StringBuilder();
        yaml.Append("name: ").Append(Scalar(workflow.Name)).Append('\n');
        if (workflow.Description is { } description)
            Text(yaml, "description", description, indent: 0, fold: true);
        if (workflow.Placeholder is { } placeholder)
            yaml.Append("placeholder: ").Append(Scalar(placeholder)).Append('\n');
        yaml.Append("starts-from: ").Append(Scalar(workflow.StartsFrom)).Append('\n');
        yaml.Append("runs-in: ").Append(Scalar(workflow.RunsIn)).Append('\n');
        yaml.Append("steps:\n");

        for (var i = 0; i < workflow.Steps.Count; i++)
        {
            if (i > 0)
                yaml.Append('\n');
            switch (workflow.Steps[i])
            {
                case WorkflowAgentStep agent:
                    WriteAgentStep(yaml, agent);
                    break;
                case WorkflowYouStep you:
                    WriteYouStep(yaml, you);
                    break;
            }
        }

        return yaml.ToString();
    }

    private static void WriteAgentStep(StringBuilder yaml, WorkflowAgentStep step)
    {
        yaml.Append("  - id: ").Append(Scalar(step.Id)).Append('\n');
        Line(yaml, "title", step.Title);
        if (step.Agent is { } agent)
            Line(yaml, "agent", agent);
        Line(yaml, "model", step.Model);
        if (step.Effort is { } effort)
            Line(yaml, "effort", effort);
        if (step.Skill is { } skill)
            Line(yaml, "skill", skill);
        if (step.Optional)
            yaml.Append("    optional: ").Append(step.OptionalHint is { Length: > 0 } hint ? Scalar(hint) : "true").Append('\n');
        if (step.Finish is { } finish)
            Line(yaml, "finish", finish);
        if (step.Writes.Count > 0)
        {
            yaml.Append("    writes:\n");
            foreach (var path in step.Writes)
                yaml.Append("      - ").Append(Scalar(path)).Append('\n');
        }

        Text(yaml, "prompt", step.Prompt, indent: 4, fold: false);
        yaml.Append("    outcomes: [").Append(string.Join(", ", step.Outcomes.Select(FlowScalar))).Append("]\n");

        // In the order of the outcomes, then any that aren't one (the parser says so), then the loop's limit.
        var on = step.Outcomes.Where(step.Routes.ContainsKey)
            .Concat(step.Routes.Keys.Where(key => !step.Outcomes.Contains(key)))
            .Select(outcome => $"{FlowScalar(outcome)}: {FlowScalar(step.Routes[outcome])}")
            .ToList();
        if (step.MaxLoops is { } max)
            on.Add($"max: {max.ToString(CultureInfo.InvariantCulture)}");
        if (on.Count > 0)
            yaml.Append("    on: { ").Append(string.Join(", ", on)).Append(" }\n");
    }

    private static void WriteYouStep(StringBuilder yaml, WorkflowYouStep step)
    {
        yaml.Append("  - id: ").Append(Scalar(step.Id)).Append('\n');
        Line(yaml, "title", step.Title);
        Line(yaml, "you", step.Ask);
        if (step.Choices.Count == 0)
        {
            yaml.Append("    choices: {}\n");
            return;
        }

        yaml.Append("    choices:\n");
        foreach (var choice in step.Choices)
        {
            yaml.Append("      ").Append(Key(choice.Label)).Append(": ");
            yaml.Append(choice.Note ? $"{{ to: {FlowScalar(choice.To)}, note: true }}" : Scalar(choice.To)).Append('\n');
        }
    }

    private static void Line(StringBuilder yaml, string key, string value)
        => yaml.Append("    ").Append(key).Append(": ").Append(Scalar(value)).Append('\n');

    /// <summary>
    /// Text that may run over lines: a literal block that keeps it exactly, a folded one for a long single line when
    /// <paramref name="fold"/>, else a scalar.
    /// </summary>
    private static void Text(StringBuilder yaml, string key, string value, int indent, bool fold)
    {
        var pad = new string(' ', indent);
        yaml.Append(pad).Append(key).Append(':');
        if (value.Contains('\n') && LiteralBlock(value, indent + 2) is { } literal)
        {
            yaml.Append(literal);
            return;
        }

        if (fold && !value.Contains('\n') && value.Length > FoldWidth && Folded(value, indent + 2) is { } folded)
        {
            yaml.Append(folded);
            return;
        }

        if (!fold && value.Length > 0 && LiteralBlock(value, indent + 2) is { } single)
        {
            // A prompt is always a block, however short.
            yaml.Append(single);
            return;
        }

        yaml.Append(' ').Append(Scalar(value)).Append('\n');
    }

    /// <summary>
    /// <c> |</c> and the lines, with the chomping that gives back exactly the trailing line breaks there were, and an
    /// indentation indicator when the first line starts with a space. Null when a literal block can't hold it.
    /// </summary>
    private static string? LiteralBlock(string value, int indent)
    {
        var body = value.TrimEnd('\n');
        var trailing = value.Length - body.Length;
        if (body.Length == 0 || !Printable(body, allowBreaks: true))
            return null;

        var lines = body.Split('\n');

        // A line of only spaces or a tab where indentation goes would be read differently.
        if (lines.Any(line => line.Length > 0 && (line.Trim(' ').Length == 0 || line[0] == '\t')))
            return null;

        var header = new StringBuilder(" |");
        if (lines.Any(line => line.StartsWith(' ')))
            header.Append('2');
        header.Append(trailing switch { 0 => "-", 1 => "", _ => "+" });
        header.Append('\n');

        var pad = new string(' ', indent);
        foreach (var line in lines)
        {
            if (line.Length > 0)
                header.Append(pad).Append(line);
            header.Append('\n');
        }

        // Kept line breaks past the first are empty lines.
        for (var i = 1; i < trailing; i++)
            header.Append('\n');
        return header.ToString();
    }

    /// <summary><c> &gt;-</c> and the text broken at single spaces, which folding turns back into those spaces.</summary>
    private static string? Folded(string value, int indent)
    {
        if (value.StartsWith(' ') || value.EndsWith(' ') || value.Contains("  ", StringComparison.Ordinal)
            || !Printable(value, allowBreaks: false) || value.Contains('\t'))
        {
            return null;
        }

        var pad = new string(' ', indent);
        var text = new StringBuilder(" >-\n");
        var line = new StringBuilder();
        foreach (var word in value.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > FoldWidth)
            {
                text.Append(pad).Append(line).Append('\n');
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }

        text.Append(pad).Append(line).Append('\n');
        return text.ToString();
    }

    /// <summary>A value on the line after its key: plain when that reads back the same, else double-quoted.</summary>
    internal static string Scalar(string value) => IsPlain(value) ? value : Quoted(value);

    /// <summary>A mapping key, e.g. a choice's label.</summary>
    private static string Key(string value) => IsPlain(value) && !value.Contains(':') ? value : Quoted(value);

    /// <summary>A value inside <c>[ ]</c> or <c>{ }</c>, where commas and brackets mean something.</summary>
    private static string FlowScalar(string value) => FlowWord().IsMatch(value) && !Reserved(value) ? value : Quoted(value);

    private static bool IsPlain(string value)
    {
        if (value.Length == 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return false;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0]))
            return false;
        if (value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal) || value.EndsWith(':'))
            return false;
        if (value.Contains('\t') || !Printable(value, allowBreaks: false))
            return false;
        return !Reserved(value);
    }

    /// <summary>What another YAML reader would take as a boolean, a null or a number, so it's quoted for them.</summary>
    private static bool Reserved(string value)
        => value.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off" or "y" or "n" or "null" or "~"
           || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
           || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("0o", StringComparison.OrdinalIgnoreCase);

    private static bool Printable(string value, bool allowBreaks)
    {
        foreach (var c in value)
        {
            if (c == '\n' && allowBreaks)
                continue;
            if (c == '\t')
                continue;
            if (char.IsControl(c) || IsBreakLike(c))
                return false;
        }

        return true;
    }

    /// <summary>A byte-order mark, or a line or paragraph separator, which YAML readers treat as breaks.</summary>
    private static bool IsBreakLike(char c) => c is (char)0xFEFF or (char)0x2028 or (char)0x2029;

    private static string Quoted(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': quoted.Append("\\\\"); break;
                case '"': quoted.Append("\\\""); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                case '\0': quoted.Append("\\0"); break;
                case var other when char.IsControl(other) || IsBreakLike(other):
                    quoted.Append("\\u").Append(((int)other).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default: quoted.Append(c); break;
            }
        }

        return quoted.Append('"').ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex FlowWord();
}
