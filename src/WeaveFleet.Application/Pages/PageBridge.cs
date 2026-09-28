using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Pages;

/// <summary>
/// The agent's <c>fleet_page_show</c>: shows an HTML file it wrote in a page canvas. Fleet copies the page
/// (<see cref="IPageStore"/>) and serves the copy itself, so nothing has to run. A file is shown in one tab:
/// showing it again replaces the copy and the tab reloads. A project's page is refused, since it needs the
/// project's server (<c>fleet_app_start</c>).
/// </summary>
public sealed class PageBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    ICanvasService canvases,
    IPageStore pages,
    ISessionRepository sessions)
{
    public const string PathRequirement = "\"path\" must name an .html file you wrote, e.g. \"/tmp/mockups/settings/options.html\".";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Task<CanvasResult<CanvasToolOutput>> ShowAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? path,
        string? title,
        CancellationToken ct = default)
        => RunAsync(bridgeToken, harnessSessionId, async sessionId =>
        {
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

            var tabs = await PageTabsAsync(sessionId, ct);
            var sameFile = tabs.FirstOrDefault(tab => string.Equals(tab.State.Source, file, PathComparison));
            var pageId = sameFile?.State.PageId ?? PageIds.New();

            var copied = await pages.CopyAsync(sessionId, pageId, file, ct);
            if (copied.Page is not { } copy)
                return Invalid(copied.Problem ?? "Fleet couldn't copy the page.");

            var warnings = PageRules.FindBrokenLinks(html)
                .Select(link => $"{link} won't load: use a path relative to the page, inside its folder.")
                .ToList();
            var state = new PageState
            {
                PageId = copy.PageId,
                Entry = copy.Entry,
                Source = file,
                Files = copy.Files,
                Bytes = copy.Bytes,
                ShownAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Warnings = warnings,
            };

            // The tab that shows this file keeps its title. Otherwise the title picks the tab, as for any canvas.
            var name = sameFile?.Canvas.Title ?? BrowserPreviews.TitleOr(title, Path.GetFileNameWithoutExtension(file));
            var replaced = sameFile is null ? tabs.FirstOrDefault(tab => tab.Canvas.Title == name)?.State.PageId : null;
            var opened = await canvases.OpenAsync(sessionId, CanvasKinds.Page, name, JsonNode.Parse(state.ToJson()), ct);
            if (!opened.IsSuccess)
            {
                if (sameFile is null)
                    await pages.DeleteAsync(sessionId, pageId, ct);
                return CanvasResult.Fail<CanvasToolOutput>(opened.Error);
            }

            if (replaced is not null && replaced != pageId)
                await pages.DeleteAsync(sessionId, replaced, ct);

            var canvas = opened.Value.Canvas;
            var output = new StringBuilder()
                .Append(sameFile is null ? "Showing " : "Updated ").Append(CanvasText.CanvasName(canvas))
                .Append(" from ").Append(file).Append(" (").Append(CanvasText.PageSize(copy.Files, copy.Bytes)).Append(" copied from its folder).");
            foreach (var warning in warnings)
                output.Append("\nWarning: ").Append(warning);
            output.Append("\nAfter an edit, call fleet_page_show again with the same file: the user's tab reloads by itself.")
                .Append(" To look at the page yourself, use fleet_browser_screenshot with this canvas.");

            return CanvasResult.Ok(new CanvasToolOutput($"{canvas.Title} · {copy.Entry}", output.ToString(), canvas.Id, canvas.Version));
        }, ct);

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
