import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useThemeStore } from "@/stores/theme";

interface MockMediaQueryList extends MediaQueryList {
  emitChange: () => void;
}

function createMatchMedia(matches: boolean): (query: string) => MockMediaQueryList {
  return (query: string) => {
    const listeners = new Set<(event: MediaQueryListEvent) => void>();

    return {
      matches,
      media: query,
      onchange: null,
      addEventListener: vi.fn((event: string, listener: EventListenerOrEventListenerObject) => {
        if (event === "change" && typeof listener === "function") {
          listeners.add(listener as (event: MediaQueryListEvent) => void);
        }
      }),
      removeEventListener: vi.fn((event: string, listener: EventListenerOrEventListenerObject) => {
        if (event === "change" && typeof listener === "function") {
          listeners.delete(listener as (event: MediaQueryListEvent) => void);
        }
      }),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(() => true),
      emitChange() {
        const event = { matches, media: query } as MediaQueryListEvent;
        listeners.forEach((listener) => listener(event));
      },
    };
  };
}

describe("useThemeStore", () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.dataset.theme = "";
    document.documentElement.style.colorScheme = "";
    setActivePinia(createPinia());
  });

  it("initializes from system theme and applies it to the document", () => {
    const matchMedia = vi.fn(createMatchMedia(true));
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: matchMedia,
    });

    const store = useThemeStore();
    store.initializeTheme();

    expect(store.currentTheme).toBe("system");
    expect(store.resolvedThemeId).toBe("dark");
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(document.documentElement.style.colorScheme).toBe("dark");
    expect(matchMedia).toHaveBeenCalledWith("(prefers-color-scheme: dark)");
  });

  it("persists explicit theme choices and toggles between light and dark", () => {
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn(createMatchMedia(false)),
    });

    const store = useThemeStore();
    store.setTheme("light");

    expect(store.currentTheme).toBe("light");
    expect(store.resolvedThemeId).toBe("light");
    expect(localStorage.getItem("weave:theme")).toBe("light");

    store.toggleTheme();

    expect(store.currentTheme).toBe("dark");
    expect(store.resolvedThemeId).toBe("dark");
    expect(localStorage.getItem("weave:theme")).toBe("dark");
    expect(document.documentElement.dataset.theme).toBe("dark");
  });
});

describe("useThemeStore code theme", () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.style.removeProperty("--syntax-keyword");
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn(createMatchMedia(true)),
    });
    setActivePinia(createPinia());
  });

  it("defaults to matching the app theme and writes that theme's syntax tokens on the document", () => {
    const store = useThemeStore();
    store.setTheme("nord");
    store.initializeTheme();

    expect(store.codeTheme).toBe("match");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#81a1c1");
  });

  it("persists an explicit code theme and applies it over the app theme", () => {
    const store = useThemeStore();
    store.setTheme("nord");
    store.setCodeTheme("monokai");

    expect(store.codeTheme).toBe("monokai");
    expect(localStorage.getItem("weave:code-theme")).toBe("monokai");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#f92672");
    expect(document.documentElement.style.getPropertyValue("--syntax-string")).toBe("#e6db74");
  });

  it("re-applies the syntax tokens when the app theme changes under 'match'", () => {
    const store = useThemeStore();
    store.setCodeTheme("match");
    store.setTheme("monokai");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#f92672");

    store.setTheme("github-dark");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#ff7b72");
  });

  it("reads a stored code theme back and ignores an unknown one", () => {
    localStorage.setItem("weave:code-theme", "darcula");
    expect(useThemeStore().codeTheme).toBe("darcula");

    setActivePinia(createPinia());
    localStorage.setItem("weave:code-theme", "comic-sans");
    expect(useThemeStore().codeTheme).toBe("match");
  });

  it("keeps a dark-only scheme for dark themes and uses Match theme on a light one", () => {
    const store = useThemeStore();
    store.setTheme("dark");
    store.setCodeTheme("monokai");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#f92672");

    store.setTheme("light");
    expect(store.codeTheme).toBe("monokai");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#a626a4");

    store.setTheme("dark");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#f92672");
  });
});
