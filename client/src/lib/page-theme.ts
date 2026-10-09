import { shallowRef, type ShallowRef } from "vue";

/**
 * Fleet's theme as the pages agents show see it: `--fleet-*` CSS variables and whether it's light or dark. Fleet's
 * script at the top of every page it serves (PageTheme.cs on the server, which holds the same names with the default
 * dark theme) reads it from the frame's name before the page draws, and from a `fleet:theme` message after a change.
 */
export interface PageTheme {
  appearance: "dark" | "light";
  variables: Record<string, string>;
}

/** Where a page sits: a conversation page is sized to fit and drawn on the conversation's own background. */
export type PagePlace = "conversation" | "tab";

/** Each page variable, from the app's own. */
const FROM_APP: Readonly<Record<string, string>> = {
  "--fleet-bg": "--panel-bg",
  "--fleet-surface": "--card-bg",
  "--fleet-text": "--text",
  "--fleet-muted": "--muted",
  "--fleet-border": "--border",
  "--fleet-accent": "--accent",
  "--fleet-accent-surface": "--accent-dim",
  "--fleet-accent-text": "--primary-foreground",
  "--fleet-success": "--running",
  "--fleet-warning": "--idle",
  "--fleet-danger": "--error",
  "--fleet-info": "--complete",
  "--fleet-radius": "--radius-card",
  "--fleet-font-sans": "--font-sans-stack",
  "--fleet-font-mono": "--font-mono-stack",
};

/**
 * Chart series, in order. Not the theme's accent, which is green in one theme and blue in another and could match a
 * later series: one set for dark grounds and one for light, each checked for colour-blind separation and contrast
 * against Fleet's panels.
 */
export const CHART_SERIES: Readonly<Record<PageTheme["appearance"], readonly string[]>> = {
  dark: ["#6366f1", "#d95926", "#199e70", "#c98500", "#d55181"],
  light: ["#5B6EC7", "#d95926", "#199e70", "#c98500", "#d55181"],
};

/** The theme the app is showing now, read from its own variables. */
export function readPageTheme(root: HTMLElement = document.documentElement): PageTheme {
  const style = getComputedStyle(root);
  const appearance = (root.style.colorScheme || style.colorScheme).trim() === "light" ? "light" : "dark";
  const variables: Record<string, string> = {};
  for (const [name, source] of Object.entries(FROM_APP)) {
    const value = style.getPropertyValue(source).trim();
    if (value) variables[name] = value;
  }
  CHART_SERIES[appearance].forEach((color, index) => {
    variables[`--fleet-chart-${index + 1}`] = color;
  });
  if (style.fontSize) variables["--fleet-font-size"] = style.fontSize;
  return { appearance, variables };
}

/** The frame's name, which the page reads before it draws: `fleet:` and the place and theme as JSON. */
export function pageFrameName(place: PagePlace, theme: PageTheme): string {
  return `fleet:${JSON.stringify({ place, ...theme })}`;
}

/** What a page is sent when the theme changes while it's open. */
export function pageThemeMessage(theme: PageTheme): { type: "fleet:theme" } & PageTheme {
  return { type: "fleet:theme", ...theme };
}

let current: ShallowRef<PageTheme> | null = null;

/**
 * The app's theme for pages, kept current: the theme store sets `data-theme` and the colour scheme on the root, and
 * the font size, so a change to the root's attributes is a change of theme.
 */
export function usePageTheme(): Readonly<ShallowRef<PageTheme>> {
  if (current) return current;
  const theme = shallowRef(readPageTheme());
  current = theme;
  if (typeof MutationObserver !== "undefined") {
    new MutationObserver(() => {
      const next = readPageTheme();
      if (JSON.stringify(next) !== JSON.stringify(theme.value)) theme.value = next;
    }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "style", "class"] });
  }
  return theme;
}
