using System.Text;

namespace WeaveFleet.Application.Pages;

/// <summary>
/// Fleet's look for the diagrams archify draws (the <c>fleet-diagrams</c> skill). Fleet adds <see cref="Layer"/> to a
/// page whose head says <c>&lt;meta name="generator" content="archify…"&gt;</c> when it serves it, so the file on disk
/// stays plain archify and every harness gets the same result.
/// <para>
/// The layer maps archify's colour variables onto the <c>--fleet-*</c> ones <see cref="PageTheme"/> sets, mirrors
/// Fleet's light or dark onto archify's <c>data-theme</c>, hides archify's toolbar and title (the tab has its own), and
/// fits the drawing to the panel. It comes after archify's own styles, so it wins the cascade.
/// </para>
/// </summary>
public static class ArchifyTheme
{
    private static readonly byte[] Generator = Encoding.UTF8.GetBytes("<meta name=\"generator\" content=\"archify");

    /// <summary>The style and script that put an archify page in Fleet's theme.</summary>
    public static readonly string Layer = """
        <style id="fleet-archify">
        /* Archify diagrams in Fleet's theme: archify's variables, drawn from the --fleet-* ones Fleet gives every page. */
        html:root, html:root[data-theme], html:root[data-theme][data-preset] {
          --bg: var(--fleet-bg);
          --grid: transparent;
          --canvas-dot: color-mix(in srgb, var(--fleet-text) 9%, transparent);
          --text: var(--fleet-text);
          --text-muted: var(--fleet-muted);
          --text-dim: color-mix(in srgb, var(--fleet-muted) 70%, var(--fleet-bg));
          --text-faint: var(--fleet-muted);
          --panel: var(--fleet-surface);
          --panel-border: var(--fleet-border);
          --lane-fill: color-mix(in srgb, var(--fleet-text) 2.5%, transparent);
          --lane-stroke: color-mix(in srgb, var(--fleet-text) 14%, transparent);
          --arrow: color-mix(in srgb, var(--fleet-muted) 85%, var(--fleet-bg));
          --arrow-emphasis: var(--fleet-accent);
          --mask: var(--fleet-bg);
          --frontend-stroke: var(--fleet-info);
          --frontend-fill: color-mix(in srgb, var(--fleet-info) 11%, var(--fleet-bg));
          --backend-stroke: var(--fleet-accent);
          --backend-fill: color-mix(in srgb, var(--fleet-accent) 11%, var(--fleet-bg));
          --database-stroke: var(--fleet-chart-3);
          --database-fill: color-mix(in srgb, var(--fleet-chart-3) 11%, var(--fleet-bg));
          --cloud-stroke: var(--fleet-chart-4);
          --cloud-fill: color-mix(in srgb, var(--fleet-chart-4) 11%, var(--fleet-bg));
          --security-stroke: var(--fleet-danger);
          --security-fill: color-mix(in srgb, var(--fleet-danger) 10%, var(--fleet-bg));
          --messagebus-stroke: var(--fleet-chart-2);
          --messagebus-fill: color-mix(in srgb, var(--fleet-chart-2) 11%, var(--fleet-bg));
          --external-stroke: var(--fleet-muted);
          --external-fill: color-mix(in srgb, var(--fleet-muted) 9%, var(--fleet-bg));
          --toolbar-bg: var(--fleet-surface);
          --toolbar-border: var(--fleet-border);
          --toolbar-text: var(--fleet-text);
          --toolbar-hover: color-mix(in srgb, var(--fleet-text) 6%, var(--fleet-surface));
          --toolbar-menu-bg: var(--fleet-surface);
          --reach-color: var(--fleet-accent);
          --lens-color: var(--fleet-accent);
          --guide-accent: var(--fleet-accent);
        }
        html:root, html:root body { background: var(--fleet-bg); font-family: var(--fleet-font-sans); }
        html:root { padding: 0 !important; }
        /* Fleet's tab already has the title and its own controls. */
        html:root .toolbar, html:root .container > .header { display: none !important; }
        html:root .container { padding-top: 12px; }
        html:root .diagram-container { background: var(--fleet-bg); border-color: var(--fleet-border); border-radius: var(--fleet-radius); box-shadow: none; }
        /* Fit wide diagrams to the panel instead of cutting them off. */
        html:root .diagram-container > svg { min-width: 0 !important; max-width: 100% !important; width: 100% !important; height: auto; }
        html:root svg text { font-family: var(--fleet-font-sans); }
        html:root .pulse-dot { display: none; }
        /* archify's zoom and route bar doesn't fit a narrow panel; the diagram is already fitted there. */
        @media (max-width: 560px) { html:root .diagram-nav { display: none !important; } }
        </style>
        <script id="fleet-archify-theme">(() => {
          // Follow Fleet's light or dark, not archify's own switch or the system setting.
          const root = document.documentElement;
          const sync = () => {
            const appearance = root.dataset.fleetAppearance || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
            if (root.dataset.theme !== appearance) root.dataset.theme = appearance;
            if (root.dataset.preset !== "classic") root.dataset.preset = "classic";
          };
          sync();
          new MutationObserver(sync).observe(root, { attributes: true, attributeFilter: ["data-fleet-appearance", "data-theme", "data-preset"] });
        })();</script>
        """.ReplaceLineEndings("\n");

    internal static readonly byte[] LayerBytes = Encoding.UTF8.GetBytes(Layer);

    /// <summary>Whether <paramref name="head"/> (the page up to its body) names archify as the generator.</summary>
    internal static bool IsArchify(ReadOnlySpan<byte> head) => head.IndexOf(Generator) >= 0;
}
