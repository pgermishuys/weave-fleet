import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RelativeTime } from "@/components/ui/relative-time";
import { formatDateTime } from "@/lib/format";

const NOW = Date.UTC(2026, 10, 14, 12, 0, 0);

describe("RelativeTime", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("says how long ago, with the exact time on hover and in datetime", () => {
    const at = NOW - 3 * 3_600_000;
    const wrapper = mount(RelativeTime, { props: { at, compact: true } });
    const time = wrapper.get("time");
    expect(time.text()).toBe("3h");
    expect(time.attributes("title")).toBe(formatDateTime(at, { now: NOW }));
    expect(time.attributes("datetime")).toBe(new Date(at).toISOString());
  });

  it("uses words without compact, and the caller's now when it passes one", () => {
    const wrapper = mount(RelativeTime, { props: { at: NOW - 2 * 60_000, now: NOW + 3 * 60_000 } });
    expect(wrapper.text()).toBe("5 minutes ago");
  });

  it("draws nothing for no time", () => {
    const wrapper = mount(RelativeTime, { props: { at: null } });
    expect(wrapper.find("time").exists()).toBe(false);
  });
});
