using WeaveFleet.Application.Canvases;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// Shows the user the page an agent opened in its own tab: the browser canvas that already shows that app (same
/// address and port), brought to the front, or a new one. The canvas offers Agent's view of the agent's tab; the
/// user's own view stays theirs. Only pages on this machine get a canvas: that's all a browser canvas can show.
/// </summary>
public sealed class AgentBrowserCanvas(ICanvasService canvases, BrowserPreviews previews)
{
    /// <summary>The canvas now showing the page, or null when there isn't one to show it in.</summary>
    public async Task<string?> ShowAsync(string sessionId, string url, string? title, CancellationToken ct = default)
    {
        if (!LoopbackUrl.TryParse(url, out var page))
            return null;

        var origin = page.GetLeftPart(UriPartial.Authority);
        foreach (var item in await canvases.ListAsync(sessionId, ct))
        {
            var canvas = item.Canvas;
            if (canvas.Kind != CanvasKinds.Browser)
                continue;
            var shown = BrowserState.Parse(canvas.StateJson).Url;
            if (Uri.TryCreate(shown, UriKind.Absolute, out var showing)
                && string.Equals(showing.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                var focused = await canvases.FocusAsync(sessionId, canvas.Id, ct);
                return focused.IsSuccess ? canvas.Id : null;
            }
        }

        var opened = await previews.OpenPageAsync(sessionId, BrowserPreviews.TitleOr(title, BrowserPreviews.DefaultTitle), page.ToString(), appId: null, ct);
        return opened.IsSuccess ? opened.Value.Canvas.Id : null;
    }
}
