import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";

export type ThemeId =
  | "dark"
  | "light"
  | "weave-classic"
  | "black"
  | "nord"
  | "dracula"
  | "solarized-dark"
  | "solarized-light"
  | "monokai"
  | "github-dark";

export type ThemeSelection = ThemeId | "system";

export type FontFamily = "inter" | "dm-sans";

export type FontSize = "s" | "m" | "l" | "xl";

export interface FontSizeDefinition {
  id: FontSize;
  label: string;
  /** Root font-size in pixels */
  px: number;
}

export const fontSizes: readonly FontSizeDefinition[] = [
  { id: "s", label: "S", px: 13 },
  { id: "m", label: "M", px: 14 },
  { id: "l", label: "L", px: 15 },
  { id: "xl", label: "XL", px: 16 },
] as const;

export interface ThemeDefinition {
  id: ThemeId;
  label: string;
  colorScheme: "dark" | "light";
  /** Preview swatches: [background, card, accent, text] */
  swatches: readonly [string, string, string, string];
}

export const themes: readonly ThemeDefinition[] = [
  { id: "dark", label: "Default Dark", colorScheme: "dark", swatches: ["#0a0a0b", "#141416", "#6366f1", "#e4e4e7"] },
  { id: "light", label: "Light", colorScheme: "light", swatches: ["#f1f5f9", "#ffffff", "#4f46e5", "#0f172a"] },
  { id: "weave-classic", label: "Weave Classic", colorScheme: "dark", swatches: ["#0F172A", "#1E293B", "#A855F7", "#F8FAFC"] },
  { id: "black", label: "Black (OLED)", colorScheme: "dark", swatches: ["#000000", "#0A0A0A", "#A855F7", "#FAFAFA"] },
  { id: "nord", label: "Nord", colorScheme: "dark", swatches: ["#2E3440", "#3B4252", "#88C0D0", "#ECEFF4"] },
  { id: "dracula", label: "Dracula", colorScheme: "dark", swatches: ["#282A36", "#343746", "#BD93F9", "#F8F8F2"] },
  { id: "solarized-dark", label: "Solarized Dark", colorScheme: "dark", swatches: ["#002B36", "#073642", "#268BD2", "#FDF6E3"] },
  { id: "solarized-light", label: "Solarized Light", colorScheme: "light", swatches: ["#FDF6E3", "#EEE8D5", "#268BD2", "#073642"] },
  { id: "monokai", label: "Monokai", colorScheme: "dark", swatches: ["#272822", "#3E3D32", "#A6E22E", "#F8F8F2"] },
  { id: "github-dark", label: "GitHub Dark", colorScheme: "dark", swatches: ["#0D1117", "#161B22", "#58A6FF", "#E6EDF3"] },
] as const;

const themeStorageKey = "weave:theme";
const fontFamilyStorageKey = "weave:font-family";
const fontSizeStorageKey = "weave:font-size";

const fontStacks: Record<FontFamily, string> = {
  inter: '"Inter Variable", "Inter", system-ui, -apple-system, sans-serif',
  "dm-sans": '"DM Sans Variable", "DM Sans", system-ui, -apple-system, sans-serif',
};

function isValidThemeId(value: string): value is ThemeId {
  return themes.some((t) => t.id === value);
}

function isValidFontFamily(value: string): value is FontFamily {
  return value === "inter" || value === "dm-sans";
}

function isValidFontSize(value: string): value is FontSize {
  return fontSizes.some((s) => s.id === value);
}

function readStoredTheme(): ThemeSelection {
  if (typeof window === "undefined") {
    return "system";
  }

  try {
    const raw = window.localStorage.getItem(themeStorageKey);
    if (!raw) {
      // Migrate from old key
      const legacy = window.localStorage.getItem("weave:theme-mode");
      if (legacy === "light") return "light";
      if (legacy === "dark") return "dark";
      return "system";
    }
    if (raw === "system") return "system";
    return isValidThemeId(raw) ? raw : "system";
  } catch {
    return "system";
  }
}

function readStoredFontFamily(): FontFamily {
  if (typeof window === "undefined") {
    return "dm-sans";
  }

  try {
    const raw = window.localStorage.getItem(fontFamilyStorageKey);
    return raw && isValidFontFamily(raw) ? raw : "dm-sans";
  } catch {
    return "dm-sans";
  }
}

function readStoredFontSize(): FontSize {
  if (typeof window === "undefined") {
    return "m";
  }

  try {
    const raw = window.localStorage.getItem(fontSizeStorageKey);
    return raw && isValidFontSize(raw) ? raw : "m";
  } catch {
    return "m";
  }
}

function getSystemThemeId(): ThemeId {
  if (typeof window === "undefined") {
    return "dark";
  }

  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function persistTheme(theme: ThemeSelection): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(themeStorageKey, theme);
  } catch {
    // localStorage unavailable
  }
}

function persistFontFamily(fontFamily: FontFamily): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(fontFamilyStorageKey, fontFamily);
  } catch {
    // localStorage unavailable
  }
}

function persistFontSize(fontSize: FontSize): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(fontSizeStorageKey, fontSize);
  } catch {
    // localStorage unavailable
  }
}

export const useThemeStore = defineStore("theme", () => {
  const currentTheme = shallowRef<ThemeSelection>(readStoredTheme());
  const fontFamily = shallowRef<FontFamily>(readStoredFontFamily());
  const fontSize = shallowRef<FontSize>(readStoredFontSize());

  const resolvedThemeId = computed<ThemeId>(() => {
    return currentTheme.value === "system" ? getSystemThemeId() : currentTheme.value;
  });

  const resolvedTheme = computed<ThemeDefinition>(() => {
    return themes.find((t) => t.id === resolvedThemeId.value) ?? themes[0];
  });

  function applyTheme(): void {
    if (typeof document === "undefined") {
      return;
    }

    const theme = resolvedTheme.value;
    document.documentElement.dataset.theme = theme.id;
    document.documentElement.style.colorScheme = theme.colorScheme;
  }

  function applyFontFamily(): void {
    if (typeof document === "undefined") {
      return;
    }

    document.documentElement.style.setProperty("--font-sans-stack", fontStacks[fontFamily.value]);
  }

  function applyFontSize(): void {
    if (typeof document === "undefined") {
      return;
    }

    const size = fontSizes.find((s) => s.id === fontSize.value) ?? fontSizes[1];
    document.documentElement.style.fontSize = `${size.px}px`;
  }

  function initializeTheme(): void {
    applyTheme();
    applyFontFamily();
    applyFontSize();

    if (typeof window === "undefined") {
      return;
    }

    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)");
    const handleSystemThemeChange = (): void => {
      if (currentTheme.value === "system") {
        applyTheme();
      }
    };

    mediaQuery.removeEventListener?.("change", handleSystemThemeChange);
    mediaQuery.addEventListener?.("change", handleSystemThemeChange);
  }

  function setTheme(theme: ThemeSelection): void {
    currentTheme.value = theme;
    persistTheme(theme);
    applyTheme();
  }

  function setFontFamily(font: FontFamily): void {
    fontFamily.value = font;
    persistFontFamily(font);
    applyFontFamily();
  }

  function setFontSize(size: FontSize): void {
    fontSize.value = size;
    persistFontSize(size);
    applyFontSize();
  }

  function toggleTheme(): void {
    const nextColorScheme = resolvedTheme.value.colorScheme === "dark" ? "light" : "dark";
    setTheme(nextColorScheme);
  }

  return {
    currentTheme,
    fontFamily,
    fontSize,
    resolvedThemeId,
    resolvedTheme,
    initializeTheme,
    setFontFamily,
    setFontSize,
    setTheme,
    toggleTheme,
  };
});
