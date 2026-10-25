import { shallowRef, type ShallowRef } from "vue";
import { CHART_SERIES, FROM_APP, type PageTheme } from "@/lib/page-theme-core";

export { CHART_SERIES, pageFrameName, pageThemeMessage, type PagePlace, type PageTheme } from "@/lib/page-theme-core";

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
