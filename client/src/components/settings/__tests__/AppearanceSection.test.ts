import { enableAutoUnmount, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import AppearanceSection from "@/components/settings/AppearanceSection.vue";
import { useThemeStore } from "@/stores/theme";

enableAutoUnmount(afterEach);

describe("AppearanceSection code theme", () => {
  beforeEach(() => {
    localStorage.clear();
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn(() => ({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
    });
    setActivePinia(createPinia());
  });

  it("lists Match theme and the six schemes, with the current one checked", () => {
    const wrapper = mount(AppearanceSection);
    const cards = wrapper.findAll("[data-code-theme]");
    expect(cards.map((c) => c.attributes("data-code-theme"))).toEqual([
      "match", "darcula", "monokai", "github", "one-dark", "solarized", "nord",
    ]);
    expect(cards.map((c) => c.text())).toEqual([
      expect.stringContaining("Match theme"),
      expect.stringContaining("Darcula"),
      expect.stringContaining("Monokai"),
      expect.stringContaining("GitHub"),
      expect.stringContaining("One Dark"),
      expect.stringContaining("Solarized"),
      expect.stringContaining("Nord"),
    ]);
    expect(wrapper.find("[data-code-theme='match']").attributes("aria-pressed")).toBe("true");
    expect(wrapper.find("[data-code-theme='nord']").attributes("aria-pressed")).toBe("false");
  });

  it("picking a scheme stores it and applies its colours", async () => {
    const wrapper = mount(AppearanceSection);
    await wrapper.find("[data-code-theme='monokai']").trigger("click");

    expect(useThemeStore().codeTheme).toBe("monokai");
    expect(localStorage.getItem("weave:code-theme")).toBe("monokai");
    expect(document.documentElement.style.getPropertyValue("--syntax-keyword")).toBe("#f92672");
    expect(wrapper.find("[data-code-theme='monokai']").attributes("aria-pressed")).toBe("true");
  });

  it("each card previews code in that scheme's own colours", () => {
    const wrapper = mount(AppearanceSection);
    const keyword = wrapper.find("[data-code-theme='darcula'] [data-token='keyword']");
    expect(keyword.exists()).toBe(true);
    expect(keyword.attributes("style")).toContain("color: rgb(204, 120, 50)");
  });
});
