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
