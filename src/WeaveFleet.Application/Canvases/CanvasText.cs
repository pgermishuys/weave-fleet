using System.Text;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

/// <summary>
/// The short text the agent reads back from canvas tools.
/// </summary>
public static class CanvasText
{
    /// <summary>The whole canvas as compact text, for <c>fleet_canvas_read</c>.</summary>
    public static string RenderFull(Canvas canvas)
    {
        var text = new StringBuilder();
        text.Append(canvas.Kind).Append(' ').Append(canvas.Id).Append(' ').Append(Quote(canvas.Title))
            .Append(" v").Append(canvas.Version);

        if (canvas.Kind == CanvasKinds.Page)
        {
            var shown = PageState.Parse(canvas.StateJson);
            text.Append("\nsource ").Append(shown.Source)
                .Append("\ncopied ").Append(PageSize(shown.Files, shown.Bytes)).Append(" at ").Append(shown.ShownAt);
            foreach (var warning in shown.Warnings)
                text.Append("\nwarning ").Append(warning);
            return text.ToString();
        }

        if (canvas.Kind == CanvasKinds.Browser)
        {
            var page = BrowserState.Parse(canvas.StateJson);
            text.Append("\nurl ").Append(page.Url);
            if (page.AppId is not null)
                text.Append("\napp ").Append(page.AppId);
            return text.ToString();
        }

        return text.ToString();
    }

    /// <summary>One line for <c>fleet_canvas_list</c>.</summary>
    public static string RenderListLine(Canvas canvas)
        => $"{canvas.Id} {canvas.Kind} {Quote(canvas.Title)} v{canvas.Version}";

    /// <summary>A short summary of applied ops for tool cards, e.g. "http://localhost:5173/".</summary>
    public static string Summarize(IReadOnlyList<CanvasOp> ops)
    {
        SetPageOp? page = null;
        ShowPageOp? shownPage = null;
        foreach (var op in ops)
        {
            switch (op)
            {
                case SetPageOp set: page = set; break;
                case ShowPageOp show: shownPage = show; break;
            }
        }

        var parts = new List<string>();
        if (page is not null)
            parts.Add(page.Url);
        if (shownPage is not null)
            parts.Add(PageSize(shownPage.Page.Files, shownPage.Page.Bytes));

        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }

    /// <summary><c>4 files, 38 KB</c></summary>
    public static string PageSize(int files, long bytes)
    {
        var size = bytes < 1024 ? $"{bytes} bytes"
            : bytes < 1024 * 1024 ? $"{(bytes + 1023) / 1024} KB"
            : $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{Plural(files, "file")}, {size}";
    }

    /// <summary><c>"Session event flow" (cv_01J…)</c></summary>
    public static string CanvasName(Canvas canvas) => $"{Quote(canvas.Title)} ({canvas.Id})";

    private static string Plural(int count, string noun)
        => count == 1 ? $"{count} {noun}" : $"{count} {noun}s";

    internal static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
