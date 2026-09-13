import type { ITheme } from "@xterm/xterm";

/**
 * An xterm theme built from the active Fleet theme's CSS variables, so the
 * terminal follows every theme (light, dark, nord, dracula, …). Text, cursor
 * and selection come from Fleet's tokens; red, green, yellow, blue and magenta
 * from its status colours; the rest from a light or dark ANSI palette picked
 * by how bright the panel is. The background stays transparent: the drawer
 * paints it with CSS.
 */

interface AnsiPalette {
  black: string;
  red: string;
  green: string;
  yellow: string;
  blue: string;
  magenta: string;
  cyan: string;
  white: string;
  brightBlack: string;
  brightRed: string;
  brightGreen: string;
  brightYellow: string;
  brightBlue: string;
  brightMagenta: string;
  brightCyan: string;
  brightWhite: string;
}

const DARK: AnsiPalette = {
  black: "#3a3a44",
  red: "#f87171",
  green: "#4ade80",
  yellow: "#fbbf24",
  blue: "#a2a7fa",
  magenta: "#c4a5fa",
  cyan: "#67e8f9",
  white: "#d6d6dd",
  brightBlack: "#6b6b76",
  brightRed: "#fca5a5",
  brightGreen: "#86efac",
  brightYellow: "#fde68a",
  brightBlue: "#c7d2fe",
  brightMagenta: "#ddd6fe",
  brightCyan: "#a5f3fc",
  brightWhite: "#f5f5f7",
};

const LIGHT: AnsiPalette = {
  black: "#1a1918",
  red: "#c4302b",
  green: "#1a7f37",
  yellow: "#9a6700",
  blue: "#4557b5",
  magenta: "#8250df",
  cyan: "#0e7490",
  white: "#8a857f",
  brightBlack: "#6e6a65",
  brightRed: "#dc2626",
  brightGreen: "#16a34a",
  brightYellow: "#b45309",
  brightBlue: "#2563eb",
  brightMagenta: "#7c3aed",
  brightCyan: "#0891b2",
  brightWhite: "#34322f",
};

/** Reads one CSS variable; a variable a theme doesn't set falls back. */
type ReadVariable = (name: string) => string;

export function terminalTheme(read: ReadVariable): ITheme {
  const get = (name: string, fallback: string) => read(name).trim() || fallback;
  const panel = get("--panel-bg", "#141418");
  const palette = isLight(panel) ? LIGHT : DARK;
  const text = get("--text", palette.brightWhite);

  return {
    ...palette,
    background: "rgba(0, 0, 0, 0)",
    foreground: text,
    cursor: text,
    cursorAccent: panel,
    // Opaque on purpose: xterm blends a translucent selection over its own background, which is
    // transparent here, so a translucent colour would come out nearly black.
    selectionBackground: mix(panel, get("--accent", "#6366f1"), 0.28) ?? get("--accent-dim", "rgba(99, 102, 241, 0.25)"),
    selectionInactiveBackground: mix(panel, text, 0.14) ?? get("--accent-dim", "rgba(99, 102, 241, 0.15)"),
    red: get("--error", palette.red),
    green: get("--running", palette.green),
    yellow: get("--idle", palette.yellow),
    blue: get("--complete", palette.blue),
    magenta: get("--queued", palette.magenta),
    scrollbarSliderBackground: withAlpha(text, 0.16),
    scrollbarSliderHoverBackground: withAlpha(text, 0.28),
    scrollbarSliderActiveBackground: withAlpha(text, 0.36),
  };
}

/** `amount` of `over` painted on `base`, as an opaque #rrggbb; null when either colour can't be read. */
export function mix(base: string, over: string, amount: number): string | null {
  const a = parseColor(base);
  const b = parseColor(over);
  if (!a || !b) return null;
  const channel = (i: number) => Math.round(a[i] + (b[i] - a[i]) * amount).toString(16).padStart(2, "0");
  return `#${channel(0)}${channel(1)}${channel(2)}`;
}

/** The colour at the given opacity, as rgba(); a colour it can't read comes back unchanged. */
export function withAlpha(color: string, alpha: number): string {
  const rgb = parseColor(color);
  return rgb ? `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${alpha})` : color;
}

/** The theme for the document's current Fleet theme. */
export function currentTerminalTheme(): ITheme {
  const style = getComputedStyle(document.documentElement);
  return terminalTheme((name) => style.getPropertyValue(name));
}

/** The monospace stack the rest of Fleet uses. */
export function currentTerminalFont(): string {
  const stack = getComputedStyle(document.documentElement).getPropertyValue("--font-mono-stack").trim();
  return stack || '"JetBrains Mono Variable", "JetBrains Mono", ui-monospace, monospace';
}

/** True for a colour brighter than mid-grey. Understands #rgb, #rrggbb and rgb()/rgba(). */
export function isLight(color: string): boolean {
  const rgb = parseColor(color);
  if (!rgb) return false;
  const [r, g, b] = rgb.map((channel) => {
    const value = channel / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.4;
}

function parseColor(color: string): [number, number, number] | null {
  const value = color.trim();
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(value);
  if (hex) {
    const digits = hex[1].length === 3 ? [...hex[1]].map((d) => d + d).join("") : hex[1];
    return [0, 2, 4].map((i) => parseInt(digits.slice(i, i + 2), 16)) as [number, number, number];
  }
  const rgb = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)/i.exec(value);
  if (rgb) return [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])];
  return null;
}
