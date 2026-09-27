using WeaveFleet.Application.Reports;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Help → Report a problem: prepare a report (gathered and with private details replaced), then send the reviewed
/// report to the Fleet maintainers or save it as a file.
/// </summary>
public static class ReportEndpoints
{
    private const int MaxTitle = 200;
    private const int MaxBody = 40_000;
    private const int MaxLog = 1_000_000;
    // A 4 MB image as base64, plus room for a data: prefix.
    private const int MaxScreenshot = 5_600_000;
    private const int MaxContact = 200;

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports");

        // POST /api/reports/prepare — gathers what the report carries now and returns it as it would be sent.
        group.MapPost("/prepare", async (PrepareReportRequest request, ProblemReportService reports, CancellationToken ct) =>
        {
            if (!ProblemReportComposer.Kinds.Contains(request.Kind))
                return Results.BadRequest(new ApiErrorResponse($"Kind must be one of {string.Join(", ", ProblemReportComposer.Kinds)}."));
            if (string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new ApiErrorResponse("Say what happened first."));

            return Results.Ok(await reports.PrepareAsync(request, ct));
        })
        .Produces<PreparedReport>()
        .WithName("PrepareReport");

        // POST /api/reports/send — sends the reviewed report to the maintainers' inbox (Fleet:Reports:InboxUrl).
        group.MapPost("/send", async (SendReportRequest request, ProblemReportService reports, CancellationToken ct) =>
        {
            if (Validate(request) is { } invalid) return invalid;
            try
            {
                return Results.Ok(new SendReportResponse(await reports.SendAsync(request, ct)));
            }
            catch (ReportSendException ex)
            {
                return Results.Json(new ApiErrorResponse(ex.Message), ApiJsonContext.Default.ApiErrorResponse, statusCode: ex.StatusCode);
            }
        })
        .Produces<SendReportResponse>()
        .WithName("SendReport");

        // POST /api/reports/file — the same report as a zip (report.md, fleet.log, screenshot), to share some other way.
        group.MapPost("/file", (SendReportRequest request, TimeProvider time) =>
        {
            if (Validate(request) is { } invalid) return invalid;
            var name = $"fleet-report-{time.GetUtcNow():yyyy-MM-dd-HHmm}.zip";
            return Results.File(ProblemReportService.BuildArchive(request), "application/zip", name);
        })
        .WithName("SaveReportFile");

        return app;
    }

    private static IResult? Validate(SendReportRequest request)
    {
        if (!ProblemReportComposer.Kinds.Contains(request.Kind))
            return Results.BadRequest(new ApiErrorResponse($"Kind must be one of {string.Join(", ", ProblemReportComposer.Kinds)}."));
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitle)
            return Results.BadRequest(new ApiErrorResponse($"The title must be 1 to {MaxTitle} characters."));
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > MaxBody)
            return Results.BadRequest(new ApiErrorResponse($"The report must be 1 to {MaxBody:N0} characters."));
        if (request.Log is { Length: > MaxLog })
            return Results.BadRequest(new ApiErrorResponse("The log is too long."));
        if (request.Screenshot is { Length: > MaxScreenshot })
            return Results.BadRequest(new ApiErrorResponse("The screenshot is over 4 MB. Remove it and try again."));
        if (request.Contact is { Length: > MaxContact })
            return Results.BadRequest(new ApiErrorResponse($"The contact must be at most {MaxContact} characters."));
        return null;
    }
}

#pragma warning restore IL2026
