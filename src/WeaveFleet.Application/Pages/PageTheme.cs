using System.Text;

namespace WeaveFleet.Application.Pages;

/// <summary>
/// Fleet's theme for the pages agents show. Fleet puts <see cref="Bootstrap"/> at the top of every HTML file it serves
/// under <c>/pages</c>, so a page can style itself with Fleet's colours and fonts as <c>--fleet-*</c> CSS variables,
/// whichever theme the user picked, and follow a change of theme while it's open.
/// <para>
/// The frame that shows the page names it <c>fleet:{json}</c> (<c>place</c>, <c>appearance</c>, <c>variables</c>):
/// a frame's name is the one thing a sandboxed page can read before it draws, so it never flashes the wrong theme.
/// Later changes come as a <c>fleet:theme</c> message from that frame. Opened on its own, the page gets the
/// default dark theme below. A page in the conversation (place <c>conversation</c>) also gets a transparent
/// background, Fleet's text colour and font, and reports its height the way MCP Apps do
/// (<c>ui/notifications/size-changed</c>), so its frame grows to fit it. A page that uses the variables
/// (<see cref="ThemedBootstrap"/>) gets Fleet's background, text colour and font in a tab, or opened on its own, too,
/// so a page written for the conversation reads there as well. Any other page gets only the variables: pages written
/// before there were any keep looking as they did.
/// </para>
/// </summary>
public static class PageTheme
{
    /// <summary>
    /// The default dark theme, what a page gets until Fleet tells it the user's. The names are the ones the client
    /// sends (client/src/lib/page-theme.ts) and the tool's description lists.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Defaults =
    [
        new("--fleet-bg", "#141418"),
        new("--fleet-surface", "#1b1b20"),
        new("--fleet-text", "#e8e8ec"),
        new("--fleet-muted", "#8e8e9a"),
        new("--fleet-border", "rgba(255, 255, 255, 0.075)"),
        new("--fleet-accent", "#6366f1"),
        new("--fleet-accent-surface", "rgba(99, 102, 241, 0.15)"),
        new("--fleet-accent-text", "#ffffff"),
        new("--fleet-success", "#22c55e"),
        new("--fleet-warning", "#f59e0b"),
        new("--fleet-danger", "#ef4444"),
        new("--fleet-info", "#3b82f6"),
        new("--fleet-chart-1", "#6366f1"),
        new("--fleet-chart-2", "#d95926"),
        new("--fleet-chart-3", "#199e70"),
        new("--fleet-chart-4", "#c98500"),
        new("--fleet-chart-5", "#d55181"),
        new("--fleet-radius", "10px"),
        new("--fleet-font-size", "14px"),
        new("--fleet-font-sans", "\"Inter Variable\", \"Inter\", system-ui, -apple-system, sans-serif"),
        new("--fleet-font-mono", "\"JetBrains Mono Variable\", \"JetBrains Mono\", ui-monospace, monospace"),
    ];

    /// <summary>
    /// The style and script Fleet adds to a page. Values are checked before they become CSS, and only Fleet's own
    /// variables are set, so a message can't restyle anything else on the page.
    /// </summary>
    public static readonly string Bootstrap = BootstrapFor(themed: false);

    /// <summary>What a page that uses the variables gets: Fleet's ground and text wherever it's opened.</summary>
    public static readonly string ThemedBootstrap = BootstrapFor(themed: true);

    private static string BootstrapFor(bool themed) => $$"""
        <style id="fleet-theme">:root{{{string.Join(";", Defaults.Select(pair => $"{pair.Key}:{pair.Value}"))}}}</style>
        <script id="fleet-page">(() => {
          const style = document.getElementById("fleet-theme"), root = document.documentElement;
          const themed = {{(themed ? "true" : "false")}};
          let place = "";
          const apply = (theme) => {
            if (!theme || typeof theme.variables !== "object" || !theme.variables) return;
            const rules = Object.entries(theme.variables)
              .filter(([name, value]) => /^--fleet-[a-z0-9-]+$/.test(name) && typeof value === "string" && !/[;{}<>]/.test(value))
              .map(([name, value]) => name + ":" + value);
            if (rules.length) style.textContent = ":root{" + rules.join(";") + "}";
            if (theme.appearance !== "dark" && theme.appearance !== "light") return;
            root.dataset.fleetAppearance = theme.appearance;
            if (place === "conversation" || themed) root.style.colorScheme = theme.appearance;
          };
          try {
            if (window.name.startsWith("fleet:")) {
              const named = JSON.parse(window.name.slice(6));
              place = named && named.place === "conversation" ? "conversation" : "tab";
              apply(named);
            }
          } catch {}
          if (place === "conversation" || themed) {
            const conversation = place === "conversation";
            root.dataset.fleetPlace = place || "page";
            if (!root.dataset.fleetAppearance) root.style.colorScheme = "dark";
            const base = document.createElement("style");
            base.textContent = "html,body{margin:0;color:var(--fleet-text);font-size:var(--fleet-font-size);line-height:1.5;font-family:var(--fleet-font-sans)}"
              + (conversation ? "html,body{background:transparent}" : "html{background:var(--fleet-bg);padding:16px 20px}");
            document.currentScript.after(base);
          }
          if (place === "conversation") {
            let sent = 0;
            const report = () => {
              const height = Math.ceil(root.getBoundingClientRect().height);
              if (height === sent) return;
              sent = height;
              parent.postMessage({ jsonrpc: "2.0", method: "ui/notifications/size-changed", params: { height } }, "*");
            };
            new ResizeObserver(report).observe(root);
          }
          addEventListener("message", (event) => {
            if (event.source === parent && event.data && event.data.type === "fleet:theme") apply(event.data);
          });
        })();</script>
        """.ReplaceLineEndings("\n");

    private static readonly byte[] BootstrapBytes = Encoding.UTF8.GetBytes(Bootstrap);
    private static readonly byte[] ThemedBootstrapBytes = Encoding.UTF8.GetBytes(ThemedBootstrap);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// <paramref name="html"/> with <see cref="Bootstrap"/> at the start of its head, or <see cref="ThemedBootstrap"/>
    /// when the page uses a <c>var(--fleet-…)</c>: after <c>&lt;head&gt;</c>, else after
    /// <c>&lt;html&gt;</c>, else after the doctype (before it, the page would lose standards mode), else at the start.
    /// Tags are ASCII, so the page's own bytes stay as they are whatever its encoding, except UTF-16, which is
    /// served as it is.
    /// </summary>
    public static byte[] Inject(ReadOnlySpan<byte> html)
    {
        if (html.Length >= 2 && ((html[0] == 0xFF && html[1] == 0xFE) || (html[0] == 0xFE && html[1] == 0xFF)))
            return html.ToArray();

        var start = html.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        var at = InsertionPoint(html, start);
        var bootstrap = html.IndexOf("var(--fleet-"u8) >= 0 ? ThemedBootstrapBytes : BootstrapBytes;
        var result = new byte[html.Length + bootstrap.Length];
        html[..at].CopyTo(result);
        bootstrap.CopyTo(result, at);
        html[at..].CopyTo(result.AsSpan(at + bootstrap.Length));
        return result;
    }

    private static int InsertionPoint(ReadOnlySpan<byte> html, int start)
    {
        // Only what comes before the body: a "<head" in the page's own text or scripts isn't its head.
        var body = IndexOfTag(html, start, "body"u8);
        var head = html[..(body < 0 ? html.Length : body)];
        foreach (var tag in new[] { "head"u8.ToArray(), "html"u8.ToArray() })
        {
            var open = IndexOfTag(head, start, tag);
            if (open >= 0 && head[open..].IndexOf((byte)'>') is var close and >= 0)
                return open + close + 1;
        }

        var doctype = IndexOfIgnoreCase(head, start, "<!doctype"u8);
        if (doctype >= 0 && head[doctype..].IndexOf((byte)'>') is var end and >= 0)
            return doctype + end + 1;
        return start;
    }

    /// <summary>Where <c>&lt;{name}</c> opens a tag (followed by <c>&gt;</c>, a space or a slash), or -1.</summary>
    private static int IndexOfTag(ReadOnlySpan<byte> html, int start, ReadOnlySpan<byte> name)
    {
        Span<byte> open = stackalloc byte[name.Length + 1];
        open[0] = (byte)'<';
        name.CopyTo(open[1..]);
        for (var from = start; ;)
        {
            var at = IndexOfIgnoreCase(html, from, open);
            if (at < 0)
                return -1;
            var next = at + open.Length;
            if (next >= html.Length || html[next] is (byte)'>' or (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'/')
                return at;
            from = at + 1;
        }
    }

    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> html, int start, ReadOnlySpan<byte> value)
    {
        for (var i = start; i <= html.Length - value.Length; i++)
        {
            var match = true;
            for (var j = 0; j < value.Length && match; j++)
                match = char.ToLowerInvariant((char)html[i + j]) == char.ToLowerInvariant((char)value[j]);
            if (match)
                return i;
        }

        return -1;
    }

    /// <summary>Whether Fleet adds its theme to <paramref name="file"/> when it serves it.</summary>
    public static bool Themes(string file)
        => file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
}
