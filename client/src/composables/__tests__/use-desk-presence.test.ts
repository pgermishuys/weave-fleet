import { defineComponent, h } from "vue";
import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const setPresence = vi.fn();
vi.mock("@/composables/use-weave-socket", () => ({
  setPresence: (...args: unknown[]) => setPresence(...args),
  onReconnect: () => () => undefined,
}));

import { PRESENCE_INTERVAL_MS, currentFormFactor, useDeskPresence } from "../use-desk-presence";

/** Presence is the interface's: it goes to the live machine. */
const live = expect.objectContaining({ key: "home", isLive: true });

function mockMatchMedia(matches: (query: string) => boolean): void {
  window.matchMedia = ((query: string) => ({ matches: matches(query), media: query, addEventListener() {}, removeEventListener() {} })) as unknown as typeof window.matchMedia;
}

describe("useDeskPresence", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    setPresence.mockReset();
    mockMatchMedia(() => false);
  });

  afterEach(() => vi.useRealTimers());

  it("reports on mount, every 30 s, and when visibility changes", () => {
    const wrapper = mount(defineComponent({ setup: () => { useDeskPresence(); return () => h("div"); } }));
    expect(setPresence).toHaveBeenLastCalledWith(live, true, "desktop");

    vi.advanceTimersByTime(PRESENCE_INTERVAL_MS);
    expect(setPresence).toHaveBeenCalledTimes(2);

    Object.defineProperty(document, "visibilityState", { value: "hidden", configurable: true });
    document.dispatchEvent(new Event("visibilitychange"));
    expect(setPresence).toHaveBeenLastCalledWith(live, false, "desktop");
    Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });

    wrapper.unmount();
    vi.advanceTimersByTime(PRESENCE_INTERVAL_MS * 3);
    expect(setPresence).toHaveBeenCalledTimes(3);
  });

  it("calls an installed app or a narrow screen a phone", () => {
    mockMatchMedia((query) => query === "(display-mode: standalone)");
    expect(currentFormFactor()).toBe("phone");
    mockMatchMedia((query) => query === "(max-width: 716px)");
    expect(currentFormFactor()).toBe("phone");
    mockMatchMedia(() => false);
    expect(currentFormFactor()).toBe("desktop");
  });
});
