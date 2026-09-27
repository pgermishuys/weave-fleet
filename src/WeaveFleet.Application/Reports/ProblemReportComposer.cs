using System.Text;

namespace WeaveFleet.Application.Reports;

/// <summary>What Fleet gathered for a report, before private details are replaced.</summary>
public sealed record ReportFacts
{
    public string FleetVersion { get; init; } = "unknown";
    /// <summary>"desktop app", "server" or "cloud".</summary>
    public string AppKind { get; init; } = "server";
    public string System { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;
    public IReadOnlyList<ReportFact> Harnesses { get; init; } = [];
    public IReadOnlyList<ReportFact> Session { get; init; } = [];
    public string? HarnessType { get; init; }
    public string? OsLabel { get; init; }
    public FleetLogExcerpt.Excerpt? Log { get; init; }
    public IReadOnlyList<ReportItemProblem> Problems { get; init; } = [];
}

/// <summary>Writes report.md and picks a title and labels. Every piece of text goes through the redactor.</summary>
public static class ProblemReportComposer
{
    public static readonly string[] Kinds = ["bug", "looks-wrong", "idea"];

    public const int MaxTitleLength = 80;

    public static PreparedReport Compose(
        PrepareReportRequest request, ReportFacts facts, ReportRedactor redactor, bool canSend)
    {
        var include = request.Include ?? new ReportIncludes();
        var client = request.Client;
        var body = new StringBuilder();

        var description = redactor.Redact(request.Description.Trim());
        body.Append("## What happened\n").Append(description).Append('\n');

        if (!string.IsNullOrWhiteSpace(request.Expected))
            body.Append("\n## Expected\n").Append(redactor.Redact(request.Expected.Trim())).Append('\n');

        if (include.Environment)
        {
            body.Append("\n## Fleet and system\n");
            Line(body, redactor, "Fleet", $"{facts.FleetVersion} · {facts.AppKind}");
            Line(body, redactor, "App", client?.App);
            Line(body, redactor, "System", facts.System);
            Line(body, redactor, "Runtime", facts.Runtime);
            foreach (var harness in facts.Harnesses)
                Line(body, redactor, harness.Name, harness.Value);
        }

        if (include.Where)
        {
            var where = new List<ReportFact>();
            if (!string.IsNullOrWhiteSpace(client?.Screen)) where.Add(new ReportFact("Screen", client.Screen));
            where.AddRange(client?.Where ?? []);
            if (!string.IsNullOrWhiteSpace(client?.ShownStatus)) where.Add(new ReportFact("Status shown", client.ShownStatus));
            where.AddRange(facts.Session);
            if (where.Count > 0)
            {
                body.Append("\n## Where\n");
                foreach (var fact in where) Line(body, redactor, fact.Name, fact.Value);
            }
        }

        if (include.Connection && client?.Connection is { Count: > 0 } connection)
        {
            body.Append("\n## Connection\n");
            foreach (var fact in connection) Line(body, redactor, fact.Name, fact.Value);
        }

        string? log = null;
        var logEntries = 0;
        if (include.Log && facts.Log is { Included: > 0 } excerpt)
        {
            log = redactor.Redact(excerpt.Text);
            logEntries = excerpt.Included;
        }

        return new PreparedReport(
            Title: TitleFrom(description),
            Body: body.ToString(),
            Log: log,
            LogEntries: logEntries,
            Labels: LabelsFor(facts),
            Replacements: redactor.Replacements.Select(r => new ReportReplacementResponse(r.Label, r.Kind, r.Shown)).ToList(),
            Problems: facts.Problems,
            CanSend: canSend);
    }

    /// <summary>The first sentence or line of the description, cut at a word near <see cref="MaxTitleLength"/>.</summary>
    public static string TitleFrom(string description)
    {
        var text = description.Trim();
        var end = text.IndexOfAny(['\n', '\r']);
        if (end >= 0) text = text[..end];
        var sentence = text.IndexOf(". ", StringComparison.Ordinal);
        if (sentence > 0) text = text[..sentence];
        text = text.TrimEnd('.', ' ');
        if (text.Length > MaxTitleLength)
        {
            var cut = text.LastIndexOf(' ', MaxTitleLength);
            text = (cut > MaxTitleLength / 2 ? text[..cut] : text[..MaxTitleLength]).TrimEnd(',', ';', ':') + "…";
        }

        return text.Length == 0 ? "Problem report" : text;
    }

    private static List<string> LabelsFor(ReportFacts facts)
    {
        var labels = new List<string>();
        if (facts.HarnessType is { Length: > 0 } harness) labels.Add(LabelText(harness));
        labels.Add(facts.AppKind switch { "desktop app" => "desktop", var kind => LabelText(kind) });
        if (facts.OsLabel is { Length: > 0 } os) labels.Add(os);
        return labels.Where(label => label.Length > 0).Distinct().Take(5).ToList();
    }

    private static string LabelText(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static void Line(StringBuilder body, ReportRedactor redactor, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        body.Append("- **").Append(redactor.Redact(name)).Append(":** ").Append(redactor.Redact(value.Trim())).Append('\n');
    }
}
