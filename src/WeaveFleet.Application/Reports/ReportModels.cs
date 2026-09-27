namespace WeaveFleet.Application.Reports;

/// <summary>What a person asks to report, plus what the window knew when they clicked.</summary>
public sealed record PrepareReportRequest(
    string Kind,
    string Description,
    string? Expected,
    string? SessionId,
    ReportIncludes? Include,
    ReportClientContext? Client);

/// <summary>Which parts to gather. The screenshot is the window's to take, so it isn't here.</summary>
public sealed record ReportIncludes(bool Environment = true, bool Where = true, bool Connection = true, bool Log = true);

/// <summary>What only the window knows.</summary>
public sealed record ReportClientContext(
    // "Desktop app" or the browser's name and version.
    string? App,
    // Where the person was, such as "Sessions" or "Settings › Harnesses".
    string? Screen,
    // The session's status as the window shows it.
    string? ShownStatus,
    IReadOnlyList<ReportFact>? Where,
    IReadOnlyList<ReportFact>? Connection,
    // Values only the window knows that must not leave, such as other machines' names and addresses.
    IReadOnlyList<ReportPrivateValue>? PrivateValues);

public sealed record ReportFact(string Name, string Value);

/// <summary>Kind is "machine" (a name) or "url" (an address).</summary>
public sealed record ReportPrivateValue(string Kind, string Value);

/// <summary>The report as it will be sent, after private details were replaced.</summary>
public sealed record PreparedReport(
    string Title,
    string Body,
    string? Log,
    int LogEntries,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ReportReplacementResponse> Replacements,
    IReadOnlyList<ReportItemProblem> Problems,
    bool CanSend);

public sealed record ReportReplacementResponse(string Label, string Kind, string Shown);

/// <summary>A part of the report that couldn't be gathered, and why.</summary>
public sealed record ReportItemProblem(string Item, string Reason);

/// <summary>A reviewed report, as the person chose to send or save it.</summary>
public sealed record SendReportRequest(
    string Kind,
    string Title,
    string Body,
    string? Log,
    // Base64 PNG or JPEG, or a data: URL.
    string? Screenshot,
    string? Contact,
    IReadOnlyList<string>? Labels);

public sealed record SendReportResponse(string Id);

/// <summary>The body Fleet posts to the report inbox (https://issues.tryweave.io/v1/reports).</summary>
public sealed record InboxReport(
    string Kind,
    string Title,
    string Body,
    string? Log,
    string? Screenshot,
    string? Contact,
    IReadOnlyList<string> Labels,
    string FleetVersion);

public sealed record InboxAccepted(string? Id);

public sealed record InboxError(string? Error);

/// <summary>Why sending failed, in words for the person sending.</summary>
public sealed class ReportSendException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
