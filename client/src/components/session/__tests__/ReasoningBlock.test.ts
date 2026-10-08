import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it } from "vitest";
import ReasoningBlock from "@/components/session/ReasoningBlock.vue";
import { useThemeStore } from "@/stores/theme";

const TEXT = "The list sorts every order in memory. A cursor would use the index.\n\nKeep the old shape for a release.";

describe("ReasoningBlock", () => {
  beforeEach(() => {
    window.localStorage.clear();
    setActivePinia(createPinia());
  });

  it("is folded to one line by default and opens on click", async () => {
    const wrapper = mount(ReasoningBlock, { props: { text: TEXT } });
    const fold = wrapper.get("[data-testid='reasoning-fold']");

    expect(fold.attributes("aria-expanded")).toBe("false");
    expect(fold.text()).toContain("Thinking");
    expect(wrapper.get("[data-testid='reasoning-gist']").text()).toBe("The list sorts every order in memory.");
    expect(wrapper.find(".reasoning-row__text").exists()).toBe(false);

    await fold.trigger("click");

    expect(fold.attributes("aria-expanded")).toBe("true");
    expect(wrapper.get(".reasoning-row__text").text()).toContain("Keep the old shape for a release.");
    expect(wrapper.find("[data-testid='reasoning-gist']").exists()).toBe(false);
    expect(wrapper.get(".reasoning-row__body").attributes("id")).toBe(fold.attributes("aria-controls"));

    await fold.trigger("click");
    expect(wrapper.find(".reasoning-row__text").exists()).toBe(false);
  });

  it("follows the newest sentence while live", () => {
    const wrapper = mount(ReasoningBlock, { props: { text: TEXT, live: true } });

    expect(wrapper.get(".reasoning-row__label").text()).toBe("Thinking…");
    expect(wrapper.find(".reasoning-row__label--live").exists()).toBe(true);
    expect(wrapper.get("[data-testid='reasoning-gist']").text()).toBe("Keep the old shape for a release.");
  });

  it("shows every block in full when set to Open", () => {
    useThemeStore().setThinking("open");
    const wrapper = mount(ReasoningBlock, { props: { text: TEXT, summary: "Paging" } });

    expect(wrapper.find("[data-testid='reasoning-fold']").exists()).toBe(false);
    expect(wrapper.get(".reasoning-row__summary").text()).toBe("Paging");
    expect(wrapper.get(".reasoning-row__text").text()).toContain("A cursor would use the index.");
  });

  it("shows nothing when set to Hidden, and remembers the choice", () => {
    useThemeStore().setThinking("hidden");
    const wrapper = mount(ReasoningBlock, { props: { text: TEXT } });

    expect(wrapper.find("[data-testid='reasoning-block']").exists()).toBe(false);
    expect(window.localStorage.getItem("weave:thinking")).toBe("hidden");

    setActivePinia(createPinia());
    expect(useThemeStore().thinking).toBe("hidden");
  });
});
