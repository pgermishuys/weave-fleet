import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { h } from "vue";
import { ErrorNote as ErrorNoteComponent } from "@/components/ui/error-note";

// CI's @vue/test-utils (2.2.7) can't type mount() of a component with named slots (TS2769).
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const ErrorNote = ErrorNoteComponent as any;

describe("ErrorNote", () => {
  it("says what went wrong and why, as an alert, in the theme's error words", () => {
    const wrapper = mount(ErrorNote, { props: { message: "Couldn't load automations.", reason: "Fleet is restarting." } });
    expect(wrapper.attributes("role")).toBe("alert");
    expect(wrapper.classes()).toContain("text-error-text");
    expect(wrapper.findAll("p").map((p) => p.text())).toEqual(["Couldn't load automations.", "Fleet is restarting."]);
    expect(wrapper.find("button").exists()).toBe(false);
  });

  it("offers Try again only when the caller listens for retry, and holds it while the retry runs", async () => {
    const retry = vi.fn();
    const wrapper = mount(ErrorNote, { props: { message: "Couldn't load the changes.", onRetry: retry } });
    await wrapper.get("button").trigger("click");
    expect(wrapper.get("button").text()).toBe("Try again");
    expect(retry).toHaveBeenCalledTimes(1);
    await wrapper.setProps({ retrying: true });
    expect(wrapper.get("button").attributes("disabled")).toBeDefined();
  });

  it("drops the tinted box when compact", () => {
    const wrapper = mount(ErrorNote, { props: { message: "That name is taken.", compact: true } });
    expect(wrapper.classes()).not.toContain("bg-error/10");
    expect(wrapper.classes()).toContain("text-xs");
  });

  it("takes rich words in the default slot and extra detail in #details", () => {
    const wrapper = mount(ErrorNote, {
      slots: { default: () => ["Fix this first: ", h("code", "on.turn")], details: () => h("ul", { class: "diag" }, "steps") },
    });
    expect(wrapper.get("code").text()).toBe("on.turn");
    expect(wrapper.find("ul.diag").exists()).toBe(true);
  });
});
