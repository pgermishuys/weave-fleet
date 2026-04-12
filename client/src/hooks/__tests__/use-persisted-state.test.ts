// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { usePersistedState } from "@/hooks/use-persisted-state";

describe("usePersistedState", () => {
  afterEach(() => {
    localStorage.clear();
  });

  it("updates when the same key changes in another tab via storage event", () => {
    const { result } = renderHook(() => usePersistedState("weave:test:key", "default"));

    expect(result.current[0]).toBe("default");

    act(() => {
      localStorage.setItem("weave:test:key", JSON.stringify("remote-value"));
      window.dispatchEvent(new StorageEvent("storage", {
        key: "weave:test:key",
        newValue: JSON.stringify("remote-value"),
        oldValue: null,
        storageArea: localStorage,
      }));
    });

    expect(result.current[0]).toBe("remote-value");
  });

  it("ignores storage events for other keys", () => {
    const { result } = renderHook(() => usePersistedState("weave:test:key", "default"));

    act(() => {
      localStorage.setItem("weave:other:key", JSON.stringify("remote-value"));
      window.dispatchEvent(new StorageEvent("storage", {
        key: "weave:other:key",
        newValue: JSON.stringify("remote-value"),
        oldValue: null,
        storageArea: localStorage,
      }));
    });

    expect(result.current[0]).toBe("default");
  });
});
