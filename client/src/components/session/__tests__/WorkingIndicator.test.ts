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

    await vi.advanceTimersByTimeAsync(46_000);
    expect(wrapper.get(".working__elapsed").text()).toBe("· 1m");

    // After the first minute only the minutes change, once a minute.
    await vi.advanceTimersByTimeAsync(30_000);
    expect(wrapper.get(".working__elapsed").text()).toBe("· 1m");
    await vi.advanceTimersByTimeAsync(30_000);
    expect(wrapper.get(".working__elapsed").text()).toBe("· 2m");

    await wrapper.setProps({ since: Date.parse("2026-09-19T08:30:00Z") });
    expect(wrapper.get(".working__elapsed").text()).toBe("· 1h 32m");
    wrapper.unmount();
    vi.useRealTimers();
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

  // Claude Code's api_retry and OpenCode 2's retry.scheduled: still in the turn, waiting out a failed model call.
  it("says it's retrying, which attempt out of how many, when and why, counting down", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-07T10:00:00Z"));
    const wrapper = mount(WorkingIndicator, {
      props: {
        since: Date.parse("2026-10-07T09:59:00Z"),
        retry: { attempt: 3, maxAttempts: 10, message: "API overloaded (529)", next: "2026-10-07T10:00:12Z" },
      },
    });

    expect(wrapper.get('[data-testid="working-retry"]').text()).toBe("Retrying · attempt 3 of 10 · in 12 s · API overloaded (529)");
    expect(wrapper.find("[aria-label='Retrying']").exists()).toBe(true);

    await vi.advanceTimersByTimeAsync(5_000);
    expect(wrapper.get('[data-testid="working-retry"]').text()).toContain("in 7 s");

    // Working again once the harness answers.
    await wrapper.setProps({ retry: null });
    expect(wrapper.text()).toContain("Working");
    expect(wrapper.find('[data-testid="working-retry"]').exists()).toBe(false);
    wrapper.unmount();
    vi.useRealTimers();
  });
});

describe("WorkingIndicator when the session's machine isn't answering", () => {
  it("says so instead of working on: no ticking dots, no growing time", () => {
    const wrapper = mount(WorkingIndicator, { props: { since: Date.now() - 60_000, notAnswering: "mini" } });

    expect(wrapper.get("[data-testid='working-not-answering']").text()).toBe("mini isn't answering. It was working when last heard.");
    expect(wrapper.find(".status-glyph--working").exists()).toBe(false);
    expect(wrapper.find(".working__elapsed").exists()).toBe(false);
  });
});
