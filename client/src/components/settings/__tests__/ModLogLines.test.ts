import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import ModLogLines from "@/components/settings/mods/ModLogLines.vue";

describe("ModLogLines", () => {
  it("says the runtime isn't running when there are no lines to show", () => {
    expect(mount(ModLogLines, { props: { lines: null } }).text()).toContain("The mod's log shows here once the mod runtime is running.");
  });

  it("says nothing was logged for an empty log", () => {
    expect(mount(ModLogLines, { props: { lines: [] } }).text()).toContain("Nothing logged yet.");
  });

  it("lists the lines with their level", () => {
    const wrapper = mount(ModLogLines, { props: { lines: [{ at: "2026-10-09T09:00:00Z", level: "error", text: "boom" }] } });
    expect(wrapper.get("[data-level=error]").text()).toContain("boom");
  });
});
