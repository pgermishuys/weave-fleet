using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Pages;

/// <summary>
/// The agent's <c>fleet_page_show</c>: shows an HTML file it wrote in a page canvas, or in the conversation. Fleet
/// copies the page (<see cref="IPageStore"/>) and serves the copy itself, so nothing has to run. A file is shown in one
/// tab: showing it again replaces the copy and the tab reloads. A page in the conversation is a copy of its own, kept
/// with the call, so every answer keeps the page it showed. A project's page is refused, since it needs the project's
/// server (<c>fleet_app_start</c>). Once the page is up, Fleet loads it (<see cref="IPageChecker"/>) and tells the agent
/// what's wrong with it in words, so it doesn't need a screenshot to find out.
/// </summary>
public sealed class PageBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ICanvasService canvases,
    IPageStore pages,
    ISessionRepository sessions,
    IPageChecker? checker = null,
    ILocalFleetUrl? fleetUrl = null)
{
    public const string PathRequirement = "\"path\" must name an .html file you wrote, e.g. \"/tmp/mockups/settings/options.html\".";

    /// <summary>In the conversation, above the agent's reply.</summary>
    public const string InConversation = "conversation";

    /// <summary>In a page tab beside the chat; what a call that names no placement gets.</summary>
    public const string InTab = "tab";

    public const string PlacementRequirement = "\"placement\" must be \"conversation\" or \"tab\".";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Task<CanvasResult<CanvasToolOutput>> ShowAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? path,
        string? title,
        string? placement = null,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
            if (!TryReadPlacement(placement, out var inConversation))
                return Invalid(PlacementRequirement);

            var file = await ResolveFileAsync(sessionId, path);
            if (file is null)
                return Invalid(PathRequirement);
            if (!File.Exists(file))
                return Invalid($"There's no file at {file}. {PathRequirement}");
            if (!PageRules.IsPageFile(file))
                return Invalid($"{file} isn't an HTML page. {PathRequirement} For a diagram, use fleet_canvas_open.");

            var folder = Path.GetDirectoryName(file)!;
            if (PageRules.FindProjectFile(folder) is { } projectFile)
                return Invalid(ProjectPageProblem(file, projectFile));

            var html = await ReadStartAsync(file, ct);
            if (PageRules.FindSourceScript(html) is { } script)
                return Invalid($"{file} loads {script}, which only a build or a dev server can run. {ProjectPageAdvice}");

            var warnings = PageRules.FindBrokenLinks(html)
                .Select(link => $"{link} won't load: use a path relative to the page, inside its folder.")
                .ToList();
            if (inConversation)
                return await ShowInConversationAsync(sessionId, file, BrowserPreviews.TitleOr(title, Path.GetFileNameWithoutExtension(file)), warnings, ct);

            var published = await PublishAsync(sessionId, file, file, BrowserPreviews.TitleOr(title, Path.GetFileNameWithoutExtension(file)), warnings, ct);
            if (!published.IsSuccess)
                return CanvasResult.Fail<CanvasToolOutput>(published.Error);

            var (canvas, copy, updated, check) = published.Value;
            var output = new StringBuilder()
                .Append(updated ? "Updated " : "Showing ").Append(CanvasText.CanvasName(canvas))
                .Append(" from ").Append(file).Append(" (").Append(CanvasText.PageSize(copy.Files, copy.Bytes)).Append(" copied from its folder).");
            foreach (var warning in warnings)
                output.Append("\nWarning: ").Append(warning);
            output.Append("\nAfter an edit, call fleet_page_show again with the same file: the user's tab reloads by itself.");
            output.Append('\n').Append(check);

            return CanvasResult.Ok(new CanvasToolOutput($"{canvas.Title} · {copy.Entry}", output.ToString(), canvas.Id, canvas.Version));
        }, ct);

    /// <summary>
    /// Shows <paramref name="file"/> in the conversation: a copy of its own, every time, so the page an answer showed
    /// stays with that answer. The tool's metadata names the copy, which the conversation loads under the call.
    /// </summary>
    private async Task<CanvasResult<CanvasToolOutput>> ShowInConversationAsync(
        string sessionId, string file, string title, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var copied = await pages.CopyAsync(sessionId, PageIds.New(), file, ct);
        if (copied.Page is not { } copy)
            return Invalid(copied.Problem ?? "Fleet couldn't copy the page.");

        var output = new StringBuilder()
            .Append("Showing ").Append(CanvasText.Quote(title)).Append(" in the conversation, above your reply, from ").Append(file)
            .Append(" (").Append(CanvasText.PageSize(copy.Files, copy.Bytes)).Append(" copied from its folder).");
        foreach (var warning in warnings)
            output.Append("\nWarning: ").Append(warning);
        output.Append("\nThe user sees the page: don't announce it, say where it is or repeat what it shows. Reply with only what it doesn't say.");
        output.Append("\nThis copy stays as it is. Showing the file again adds a new page under your next answer.");
        if (CheckText(await CheckAsync(copy, ct), inConversation: true) is { Length: > 0 } check)
            output.Append('\n').Append(check);

        return CanvasResult.Ok(new CanvasToolOutput(title, output.ToString(), Page: new PageReference(copy.PageId, copy.Entry)));
    }

    /// <summary>Where the call asked for the page: nothing, or "tab", is a tab, as before there was a choice.</summary>
    private static bool TryReadPlacement(string? placement, out bool inConversation)
    {
        var value = placement?.Trim();
        inConversation = string.Equals(value, InConversation, StringComparison.OrdinalIgnoreCase);
        return inConversation || string.IsNullOrEmpty(value) || string.Equals(value, InTab, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies <paramref name="file"/> (and the web files in its folder) into a page and shows it in the session's page tab
    /// for <paramref name="source"/>: the same source updates its tab, which reloads. Otherwise <paramref name="title"/>
    /// picks the tab, as for any canvas. Says what Fleet's check of the page found.
    /// </summary>
    internal async Task<CanvasResult<PublishedPage>> PublishAsync(
        string sessionId, string source, string file, string title, IReadOnlyList<string> warnings, CancellationToken ct, string? label = null)
    {
        var tabs = await PageTabsAsync(sessionId, ct);
        var sameSource = tabs.FirstOrDefault(tab => string.Equals(tab.State.Source, source, PathComparison));
        var pageId = sameSource?.State.PageId ?? PageIds.New();

        var copied = await pages.CopyAsync(sessionId, pageId, file, ct);
        if (copied.Page is not { } copy)
            return CanvasResult.Fail<PublishedPage>(CanvasErrorKind.Invalid, copied.Problem ?? "Fleet couldn't copy the page.");

        var state = new PageState
        {
            PageId = copy.PageId,
            Entry = copy.Entry,
            Source = source,
            Files = copy.Files,
            Bytes = copy.Bytes,
            ShownAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Warnings = [.. warnings],
            Label = label,
        };

        // The tab that shows this source keeps its title. Otherwise the title picks the tab, as for any canvas.
        var name = sameSource?.Canvas.Title ?? title;
        var replaced = sameSource is null ? tabs.FirstOrDefault(tab => tab.Canvas.Title == name)?.State.PageId : null;
        var opened = await canvases.OpenAsync(sessionId, CanvasKinds.Page, name, JsonNode.Parse(state.ToJson()), ct);
        if (!opened.IsSuccess)
        {
            if (sameSource is null)
                await pages.DeleteAsync(sessionId, pageId, ct);
            return CanvasResult.Fail<PublishedPage>(opened.Error);
        }

        if (replaced is not null && replaced != pageId)
            await pages.DeleteAsync(sessionId, replaced, ct);

        return CanvasResult.Ok(new PublishedPage(opened.Value.Canvas, copy, Updated: sameSource is not null, CheckText(await CheckAsync(copy, ct), inConversation: false)));
    }


    private async Task<PageCheckOutcome?> CheckAsync(PageCopy copy, CancellationToken ct)
        => checker is not null && fleetUrl?.TryGet() is { } fleet
            ? await checker.CheckAsync(PageRules.EntryUrl(fleet, copy.PageId, copy.Entry).AbsoluteUri, ct)
            : null;

    private static readonly string Widths = string.Join(" and ", IPageChecker.Widths) + " px wide";

    /// <summary>
    /// What the check found, and whether the agent still needs to look. A screenshot costs over a thousand tokens and
    /// the user already sees the page, so a clean check is the end of it unless the look is what the page is for.
    /// A page in the conversation has no canvas to take a screenshot of, so its text leaves the screenshot out.
    /// </summary>
    private static string CheckText(PageCheckOutcome? check, bool inConversation)
    {
        if (check is null || check.Problem is not null)
        {
            var why = check?.Problem is { } problem ? $"Fleet couldn't check the page: {problem.TrimEnd().TrimEnd('.')}." : string.Empty;
            return inConversation ? why : (why + " To look at the page yourself, use fleet_browser_screenshot with this canvas.").TrimStart();
        }

        if (check.Findings.Count == 0)
            return $"Fleet loaded the page at {Widths}: no script errors, every file loaded, nothing wider than the window."
                   + (inConversation
                       ? string.Empty
                       : " For a report, results or a document, that's enough: hand it over without a screenshot. "
                         + "Use fleet_browser_screenshot with this canvas only when how the page looks is the point, such as a mockup or a design.");

        var text = new StringBuilder("Fleet loaded the page at ").Append(Widths).Append(" and found:");
        foreach (var finding in check.Findings)
            text.Append("\n- ").Append(finding);
        return text.Append("\nFix these, then call fleet_page_show again: it checks the page again.").ToString();
    }

    private const string ProjectPageAdvice =
        "If it's the project's app, run it with fleet_app_start and its dev command (read package.json or the README first). "
        + "If it's a page you wrote, put it in a folder of its own and show it from there.";

    internal static string ProjectPageProblem(string file, string projectFile)
        => $"{file} is in a project folder ({projectFile} is next to it), so its page may need the project's server. {ProjectPageAdvice}";

    /// <summary>An absolute path as it is; a relative one from the session's folder, which is where the agent works.</summary>
    private async Task<string?> ResolveFileAsync(string sessionId, string? path)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            trimmed = uri.LocalPath;
        if (Path.IsPathFullyQualified(trimmed))
            return Path.GetFullPath(trimmed);

        var session = await sessions.GetByIdAsync(sessionId);
        return string.IsNullOrEmpty(session?.Directory) ? null : Path.GetFullPath(Path.Combine(session.Directory, trimmed));
    }

    /// <summary>The start of the page, enough to find its scripts and links.</summary>
    private static async Task<string> ReadStartAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var buffer = new byte[Math.Min(stream.Length, PageRules.MaxEntryBytesRead)];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private async Task<IReadOnlyList<PageTab>> PageTabsAsync(string sessionId, CancellationToken ct)
        => [.. (await canvases.ListAsync(sessionId, ct))
            .Select(item => item.Canvas)
            .Where(canvas => canvas.Kind == CanvasKinds.Page)
            .Select(canvas => new PageTab(canvas, PageState.Parse(canvas.StateJson)))];

    private sealed record PageTab(Canvas Canvas, PageState State);

    internal sealed record PublishedPage(Canvas Canvas, PageCopy Copy, bool Updated, string Check);

    private async Task<CanvasResult<CanvasToolOutput>> RunAsync(
        string? bridgeToken,
        string? harnessSessionId,
        Func<string, Task<CanvasResult<CanvasToolOutput>>> call,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId, ct);
        if (caller is null)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
            return await call(caller.FleetSessionId);
    }

    private static CanvasResult<CanvasToolOutput> Invalid(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, message);

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);
}
