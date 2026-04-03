"use client";

import { createContext, useContext, useEffect } from "react";
import { usePersistedState } from "@/hooks/use-persisted-state";

export type Theme =
  | "default"
  | "black"
  | "light"
  | "nord"
  | "dracula"
  | "solarized-dark"
  | "solarized-light"
  | "monokai"
  | "github-dark";

export const ALL_THEMES: Theme[] = [
  "default",
  "black",
  "light",
  "nord",
  "dracula",
  "solarized-dark",
  "solarized-light",
  "monokai",
  "github-dark",
];

export const THEME_LABELS: Record<Theme, string> = {
  default: "Default (Dark Slate)",
  black: "Black (OLED)",
  light: "Light",
  nord: "Nord",
  dracula: "Dracula",
  "solarized-dark": "Solarized Dark",
  "solarized-light": "Solarized Light",
  monokai: "Monokai",
  "github-dark": "GitHub Dark",
};

const LIGHT_THEMES: Theme[] = ["light", "solarized-light"];

interface ThemeContextValue {
  theme: Theme;
  setTheme: (theme: Theme) => void;
  /** "dark" for dark themes; "light" for light themes */
  resolvedTheme: "dark" | "light";
}

const ThemeContext = createContext<ThemeContextValue | null>(null);

const THEME_CLASSES: Record<Theme, string[]> = {
  default: ["dark"],
  black: ["dark", "theme-black"],
  light: ["theme-light"],
  nord: ["dark", "theme-nord"],
  dracula: ["dark", "theme-dracula"],
  "solarized-dark": ["dark", "theme-solarized-dark"],
  "solarized-light": ["theme-solarized-light"],
  monokai: ["dark", "theme-monokai"],
  "github-dark": ["dark", "theme-github-dark"],
};

const ALL_THEME_CLASS_NAMES = [
  "dark",
  "theme-black",
  "theme-light",
  "theme-nord",
  "theme-dracula",
  "theme-solarized-dark",
  "theme-solarized-light",
  "theme-monokai",
  "theme-github-dark",
];

function applyThemeClasses(theme: Theme) {
  const html = document.documentElement;
  html.classList.remove(...ALL_THEME_CLASS_NAMES);
  const classes = THEME_CLASSES[theme] ?? THEME_CLASSES.default;
  html.classList.add(...classes);
}

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const [theme, setTheme] = usePersistedState<Theme>("weave-theme", "default");

  useEffect(() => {
    applyThemeClasses(theme);
  }, [theme]);

  const resolvedTheme = LIGHT_THEMES.includes(theme) ? "light" : "dark";

  return (
    <ThemeContext.Provider value={{ theme, setTheme, resolvedTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme(): ThemeContextValue {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error("useTheme must be used within a ThemeProvider");
  }
  return context;
}
