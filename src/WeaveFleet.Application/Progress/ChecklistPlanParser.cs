using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Progress;

/// <summary>
/// Reads a plan out of any markdown checklist: Weave's <c>.weave/plans</c> files, superpowers plans, spec-kit
/// task lists, a hand-written <c>TODO.md</c>. Knows nothing about who wrote the plan.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Steps are unindented list items with a checkbox: <c>- [ ]</c>, <c>* [x]</c>, <c>1. [X]</c>. A leading
/// <c>N.</c> in the text is the step's number.</item>
/// <item>Indented checkboxes are sub-steps of the step above them.</item>
/// <item>A step belongs to the nearest heading above it. Headings with no steps under them are left out.</item>
/// <item>The plan's title is its first <c>#</c> heading.</item>
/// <item>Fenced code blocks are skipped, so a <c># comment</c> or <c>- [ ]</c> inside one means nothing.</item>
/// </list>
/// </remarks>
public static partial class ChecklistPlanParser
{
    public static PlanDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string? title = null;
        string? heading = null;
        var groups = new List<PlanGroup>();
        List<PlanStep>? groupSteps = null;
        StepBuilder? step = null;
        string? fence = null;

        void CloseStep()
        {
            if (step is null)
                return;
            groupSteps!.Add(step.Build());
            step = null;
        }

        void CloseGroup()
        {
            CloseStep();
            if (groupSteps is { Count: > 0 })
                groups.Add(new PlanGroup(heading, groupSteps));
            groupSteps = null;
        }

        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            // Fenced code: ``` or ~~~, closed by the same fence at least as long.
            var fenceMatch = FenceRegex().Match(line);
            if (fenceMatch.Success)
            {
                var marker = fenceMatch.Groups["fence"].Value;
                if (fence is null)
                    fence = marker;
                else if (marker[0] == fence[0] && marker.Length >= fence.Length)
                    fence = null;
                continue;
            }

            if (fence is not null)
            {
                step?.AddText(line);
                continue;
            }

            var headingMatch = HeadingRegex().Match(line);
            if (headingMatch.Success)
            {
                var text = headingMatch.Groups["text"].Value.Trim();
                if (headingMatch.Groups["hashes"].Value.Length == 1 && title is null)
                    title = text;

                CloseGroup();
                heading = text;
                continue;
            }

            var checkbox = CheckboxRegex().Match(line);
            if (checkbox.Success)
            {
                var indent = checkbox.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
                var isChecked = checkbox.Groups["mark"].Value is "x" or "X";

                if (indent >= 2)
                {
                    step?.AddSubStep(isChecked);
                    continue;
                }

                CloseStep();
                groupSteps ??= [];
                step = new StepBuilder(i + 1, checkbox.Groups["text"].Value, isChecked);
                continue;
            }

            // A new unindented list item without a checkbox ends the step; anything else belongs to it.
            if (step is not null && UnindentedListItemRegex().IsMatch(line))
                CloseStep();
            else
                step?.AddText(line);
        }

        CloseGroup();
        return new PlanDocument(title, groups);
    }

    private sealed class StepBuilder(int line, string text, bool isChecked)
    {
        private readonly List<string> _text = [text];
        private int _subDone;
        private int _subTotal;

        public void AddText(string text) => _text.Add(text);

        public void AddSubStep(bool isChecked)
        {
            _subTotal++;
            if (isChecked)
                _subDone++;
        }

        public PlanStep Build()
        {
            var raw = _text[0].Trim();
            var number = StepNumberRegex().Match(raw);
            var title = number.Success ? raw[number.Length..].Trim() : raw;
            var mentions = _text
                .SelectMany(text => CodeSpanRegex().Matches(text).Select(match => match.Groups["code"].Value.Trim()))
                .Where(LooksLikePath)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new PlanStep(
                line,
                number.Success ? number.Groups["number"].Value : null,
                title,
                isChecked,
                _subDone,
                _subTotal,
                mentions);
        }

        private static bool LooksLikePath(string code)
            => code.Length > 0
               && !code.Contains(' ', StringComparison.Ordinal)
               && (code.Contains('/', StringComparison.Ordinal) || code.Contains('\\', StringComparison.Ordinal) || Path.HasExtension(code));
    }

    [GeneratedRegex(@"^\s{0,3}(?<fence>`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\s{0,3}(?<hashes>#{1,6})\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:[-*+]|\d{1,9}[.)])\s+\[(?<mark>[ xX])\](?:\s+(?<text>.*))?$")]
    private static partial Regex CheckboxRegex();

    [GeneratedRegex(@"^(?:[-*+]|\d{1,9}[.)])\s")]
    private static partial Regex UnindentedListItemRegex();

    [GeneratedRegex(@"^(?<number>\d{1,9})[.)]\s*")]
    private static partial Regex StepNumberRegex();

    [GeneratedRegex(@"`(?<code>[^`]+)`")]
    private static partial Regex CodeSpanRegex();
}

/// <summary>A markdown checklist read as a plan.</summary>
public sealed record PlanDocument(string? Title, IReadOnlyList<PlanGroup> Groups)
{
    public IEnumerable<PlanStep> Steps => Groups.SelectMany(group => group.Steps);
}

/// <summary>Steps under one heading. <see cref="Title"/> is null for steps above the first heading.</summary>
public sealed record PlanGroup(string? Title, IReadOnlyList<PlanStep> Steps);

/// <summary>One checkbox step.</summary>
/// <param name="Line">The 1-based line the step starts on.</param>
/// <param name="Number">The number the plan gave the step (<c>11</c> in <c>- [ ] 11. …</c>), if any.</param>
/// <param name="Title">The step's text, without its number.</param>
/// <param name="Mentions">Backticked paths in the step, such as the files it lists.</param>
public sealed record PlanStep(
    int Line,
    string? Number,
    string Title,
    bool Checked,
    int SubDone,
    int SubTotal,
    IReadOnlyList<string> Mentions)
{
    /// <summary>What identifies the step from one version of the file to the next: its number, else its title.</summary>
    public string Key => Number ?? Title;
}
