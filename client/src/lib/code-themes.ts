import type { ThemeId } from "@/stores/theme";

/**
 * Code colour schemes for the Files tab editor and for code blocks in Markdown.
 *
 * Both read the eight `--syntax-*` CSS variables, so a scheme is just eight hex colours. Edit a
 * colour here and every language follows, since highlight.js and CodeMirror map their tokens onto
 * these same eight names (see assets/markdown.css and lib/code-editor/theme.ts).
 *
 *   keyword   `public`, `if`, `return`, `const`
 *   string    "text" and regexes
 *   number    1, 2.5
 *   comment   // and /* ... *\/
 *   function  method and function names
 *   attr      JSON keys, HTML attributes, object properties
 *   type      class, interface and tag names
 *   literal   true, false, null, escapes
 */
export const syntaxTokens = ["keyword", "string", "number", "comment", "function", "attr", "type", "literal"] as const;

export type SyntaxToken = (typeof syntaxTokens)[number];

export type CodePalette = Readonly<Record<SyntaxToken, string>>;

export type CodeThemeId = "darcula" | "monokai" | "github" | "one-dark" | "solarized" | "nord";

/** "match" follows the app theme: its own scheme where one exists, otherwise One Dark / One Light. */
export type CodeThemeSelection = CodeThemeId | "match";

export interface CodeThemeDefinition {
  id: CodeThemeId;
  label: string;
  /** The name to show when the light palette is in use, where it has its own (One Dark's is One Light). */
  lightLabel?: string;
  dark: CodePalette;
  /** Only schemes with a real light variant have one; the rest are for dark themes only (see isCodeThemeAvailable). */
  light?: CodePalette;
}

export const codeThemes: readonly CodeThemeDefinition[] = [
  {
    id: "darcula",
    label: "Darcula",
    dark: {
      keyword: "#cc7832",
      string: "#6a8759",
      number: "#6897bb",
      comment: "#808080",
      function: "#ffc66d",
      attr: "#9876aa",
      type: "#a9b7c6",
      literal: "#cc7832",
    },
  },
  {
    id: "monokai",
    label: "Monokai",
    dark: {
      keyword: "#f92672",
      string: "#e6db74",
      number: "#ae81ff",
      comment: "#75715e",
      function: "#a6e22e",
      attr: "#f92672",
      type: "#66d9ef",
      literal: "#ae81ff",
    },
  },
  {
    id: "github",
    label: "GitHub",
    dark: {
      keyword: "#ff7b72",
      string: "#a5d6ff",
      number: "#79c0ff",
      comment: "#8b949e",
      function: "#d2a8ff",
      attr: "#7ee787",
      type: "#ffa657",
      literal: "#79c0ff",
    },
    light: {
      keyword: "#cf222e",
      string: "#0a3069",
      number: "#0550ae",
      comment: "#6e7781",
      function: "#8250df",
      attr: "#116329",
      type: "#953800",
      literal: "#0550ae",
    },
  },
  {
    id: "one-dark",
    label: "One Dark",
    lightLabel: "One Light",
    dark: {
      keyword: "#c678dd",
      string: "#98c379",
      number: "#d19a66",
      comment: "#8b949e",
      function: "#61afef",
      attr: "#e5c07b",
      type: "#e06c75",
      literal: "#56b6c2",
    },
    light: {
      keyword: "#a626a4",
      string: "#50a14f",
      number: "#986801",
      comment: "#a0a1a7",
      function: "#4078f2",
      attr: "#c18401",
      type: "#e45649",
      literal: "#0184bc",
    },
  },
  {
    id: "solarized",
    label: "Solarized",
    dark: {
      keyword: "#859900",
      string: "#2aa198",
      number: "#d33682",
      comment: "#586e75",
      function: "#268bd2",
      attr: "#93a1a1",
      type: "#b58900",
      literal: "#cb4b16",
    },
    light: {
      keyword: "#859900",
      string: "#2aa198",
      number: "#d33682",
      comment: "#93a1a1",
      function: "#268bd2",
      attr: "#586e75",
      type: "#b58900",
      literal: "#cb4b16",
    },
  },
  {
    id: "nord",
    label: "Nord",
    dark: {
      keyword: "#81a1c1",
      string: "#a3be8c",
      number: "#b48ead",
      // Lighter than Nord's own #616e88 (2.3:1 on a Nord code block); this one reads at about 4:1.
      comment: "#909aae",
      function: "#88c0d0",
      attr: "#8fbcbb",
      type: "#8fbcbb",
      literal: "#d08770",
    },
  },
] as const;

/** The scheme an app theme brings with it under "match". Themes not listed get One Dark / One Light. */
const themeSchemes: Partial<Record<ThemeId, CodeThemeId>> = {
  nord: "nord",
  monokai: "monokai",
  "github-dark": "github",
  "solarized-dark": "solarized",
  "solarized-light": "solarized",
};

export function isValidCodeTheme(value: string): value is CodeThemeSelection {
  return value === "match" || codeThemes.some((t) => t.id === value);
}

/**
 * Whether a scheme can be used under this colour scheme. Darcula, Monokai and Nord have no light
 * colours, and their dark ones are unreadable on a light background, so they're for dark themes only.
 */
export function isCodeThemeAvailable(id: CodeThemeId, colorScheme: "dark" | "light"): boolean {
  if (colorScheme === "dark") return true;
  return codeThemes.find((t) => t.id === id)?.light !== undefined;
}

export function resolveCodeThemeId(selection: CodeThemeSelection, themeId: ThemeId, colorScheme: "dark" | "light"): CodeThemeId {
  // A dark-only scheme picked on a dark theme falls back to Match theme on a light one, and comes back on a dark one.
  if (selection !== "match" && isCodeThemeAvailable(selection, colorScheme)) return selection;
  return themeSchemes[themeId] ?? "one-dark";
}

/** The scheme's name as shown under this colour scheme: One Dark reads One Light on a light theme. */
export function codeThemeLabel(theme: CodeThemeDefinition, colorScheme: "dark" | "light"): string {
  return colorScheme === "light" && theme.light && theme.lightLabel ? theme.lightLabel : theme.label;
}

export function resolveCodePalette(selection: CodeThemeSelection, themeId: ThemeId, colorScheme: "dark" | "light"): CodePalette {
  const id = resolveCodeThemeId(selection, themeId, colorScheme);
  const theme = codeThemes.find((t) => t.id === id) ?? codeThemes[0];
  return colorScheme === "light" && theme.light ? theme.light : theme.dark;
}
