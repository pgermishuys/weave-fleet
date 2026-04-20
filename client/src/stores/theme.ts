import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";

export type ThemeMode = "light" | "dark" | "system";

const themeStorageKey = "weave:theme-mode";

function readStoredTheme(): ThemeMode {
  if (typeof window === "undefined") {
    return "system";
  }

  try {
    const raw = window.localStorage.getItem(themeStorageKey);
    return raw === "light" || raw === "dark" || raw === "system" ? raw : "system";
  } catch {
    return "system";
  }
}

function getSystemTheme(): Exclude<ThemeMode, "system"> {
  if (typeof window === "undefined") {
    return "dark";
  }

  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function persistTheme(theme: ThemeMode): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(themeStorageKey, theme);
  } catch {
    // localStorage unavailable
  }
}

export const useThemeStore = defineStore("theme", () => {
  const currentTheme = shallowRef<ThemeMode>(readStoredTheme());
  const resolvedTheme = computed<Exclude<ThemeMode, "system">>(() => {
    return currentTheme.value === "system" ? getSystemTheme() : currentTheme.value;
  });

  function applyTheme(): void {
    if (typeof document === "undefined") {
      return;
    }

    document.documentElement.dataset.theme = resolvedTheme.value;
    document.documentElement.style.colorScheme = resolvedTheme.value;
  }

  function initializeTheme(): void {
    applyTheme();

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

  function setTheme(theme: ThemeMode): void {
    currentTheme.value = theme;
    persistTheme(theme);
    applyTheme();
  }

  function toggleTheme(): void {
    setTheme(resolvedTheme.value === "dark" ? "light" : "dark");
  }

  return {
    currentTheme,
    resolvedTheme,
    initializeTheme,
    setTheme,
    toggleTheme,
  };
});
