import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import TurnRetryBar from "@/components/session/TurnRetryBar.vue";
import type { ScheduledRetry } from "@/lib/turn-retry";

const NOW = new Date(2026, 9, 7, 1, 51, 43);

function retry(overrides: Partial<ScheduledRetry> = {}): ScheduledRetry {
  return {
    dueAt: new Date(NOW.getTime() + 112 * 60_000).toISOString(),
    attempt: 1,
    kind: "usage_limit",
    reason: "You've hit your session limit",
    providerSaid: true,
    ...overrides,
  };
}

describe("TurnRetryBar", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("says when Fleet tries again, and that it waits for the limit's reset", () => {
    const wrapper = mount(TurnRetryBar, { props: { retry: retry() } });

    const clock = new Date(NOW.getTime() + 112 * 60_000).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    expect(wrapper.get("[data-testid='turn-retry-when']").text()).toBe(`Fleet tries again at ${clock} · in 1 h 52 min`);
    expect(wrapper.text()).toContain("It carries on when the limit resets. Nothing was lost.");
  });

  it("counts down, by the second in the last minute", async () => {
    const wrapper = mount(TurnRetryBar, { props: { retry: retry({ dueAt: new Date(NOW.getTime() + 75_000).toISOString() }) } });
    expect(wrapper.get("[data-testid='turn-retry-when']").text()).toContain("in 2 min");

    await vi.advanceTimersByTimeAsync(30_000);
    expect(wrapper.get("[data-testid='turn-retry-when']").text()).toContain("in 45 s");

    await vi.advanceTimersByTimeAsync(46_000);
    expect(wrapper.get("[data-testid='turn-retry-when']").text()).toBe("Fleet is trying again now");
  });

  it("says which attempt it is when the limit keeps stopping the session", () => {
    const wrapper = mount(TurnRetryBar, { props: { retry: retry({ attempt: 3, kind: "rate_limit", providerSaid: false }) } });

    expect(wrapper.text()).toContain("Attempt 3, after a wait.");
  });

  it("offers Try now and Don't retry", async () => {
    const wrapper = mount(TurnRetryBar, { props: { retry: retry() } });

    await wrapper.get("[data-testid='turn-retry-now']").trigger("click");
    await wrapper.get("[data-testid='turn-retry-cancel']").trigger("click");

    expect(wrapper.emitted("sendNow")).toHaveLength(1);
    expect(wrapper.emitted("cancel")).toHaveLength(1);
  });

  it("shows why an action failed instead of the explanation", () => {
    const wrapper = mount(TurnRetryBar, { props: { retry: retry(), error: "Fleet couldn't try again now (HTTP 500)." } });

    expect(wrapper.text()).toContain("Fleet couldn't try again now (HTTP 500).");
    expect(wrapper.text()).not.toContain("Nothing was lost.");
  });
});
