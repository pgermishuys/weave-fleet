import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { saveSessionListScroll, takeSessionListScroll } from "@/lib/session-list-scroll";

describe("session list scroll across a machine switch", () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("gives back the saved place once", () => {
    saveSessionListScroll(312);

    expect(takeSessionListScroll()).toBe(312);
    expect(takeSessionListScroll()).toBeNull();
  });

  it("ignores a place saved too long ago to be this reload's", () => {
    vi.useFakeTimers();
    saveSessionListScroll(312);
    vi.advanceTimersByTime(60_000);

    expect(takeSessionListScroll()).toBeNull();
  });

  it("has nothing when nothing was saved, or what was saved is garbled", () => {
    expect(takeSessionListScroll()).toBeNull();
    sessionStorage.setItem("weave:sessions-list-scroll", "{not json");
    expect(takeSessionListScroll()).toBeNull();
  });
});
