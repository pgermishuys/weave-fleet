import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import WorkingIndicator from "@/components/session/WorkingIndicator.vue";

describe("WorkingIndicator", () => {
  it("shows the working glyph and word", () => {
    const wrapper = mount(WorkingIndicator);

    expect(wrapper.find(".status-glyph--working").exists()).toBe(true);
    expect(wrapper.text()).toContain("Working");
    expect(wrapper.find(".working__elapsed").exists()).toBe(false);
  });

  it("counts up from the prompt", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-19T10:00:14Z"));
    const wrapper = mount(WorkingIndicator, { props: { since: Date.parse("2026-09-19T10:00:00Z") } });

    expect(wrapper.get(".working__elapsed").text()).toBe("· 14s");

    await vi.advanceTimersByTimeAsync(61_000);
    expect(wrapper.get(".working__elapsed").text()).toBe("· 1m 15s");

    await wrapper.setProps({ since: Date.parse("2026-09-19T08:30:00Z") });
    expect(wrapper.get(".working__elapsed").text()).toBe("· 1h 31m");
    wrapper.unmount();
  });

  // A question (a sub-agent's or the session's own) holds the turn: the words and diamond of the session row.
  it("says it needs input while a question waits", () => {
    const wrapper = mount(WorkingIndicator, { props: { since: Date.now() - 5000, waiting: true } });

    expect(wrapper.text()).toBe("Needs input");
    expect(wrapper.find(".status-glyph--working").exists()).toBe(false);
    expect(wrapper.find("[aria-label='Needs input']").exists()).toBe(true);
    expect(wrapper.find(".working__elapsed").exists()).toBe(false);
  });

  it("never shows a negative time when clocks disagree", () => {
    const wrapper = mount(WorkingIndicator, { props: { since: Date.now() + 5000 } });

    expect(wrapper.get(".working__elapsed").text()).toBe("· 0s");
  });
});
