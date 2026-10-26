import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";

const { get, put } = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock("@/api/client", () => ({ api: { GET: get, PUT: put } }));

import { usePreferencesStore } from "../preferences";

describe("preferences store: a save that fails", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    get.mockReset();
    put.mockReset();
    get.mockResolvedValue({ data: { Starters: "true" } });
  });

  it("goes back to the saved value when the server answers with an error", async () => {
    const store = usePreferencesStore();
    await store.refresh();
    put.mockResolvedValue({ error: { error: "boom" }, response: { status: 500 } });

    await store.set("Starters", "false");

    expect(store.get("Starters", "true")).toBe("true");
  });

  it("goes back to the last value it had when the server can't be reached at all", async () => {
    const store = usePreferencesStore();
    await store.refresh();
    put.mockRejectedValue(new TypeError("Failed to fetch"));
    get.mockRejectedValue(new TypeError("Failed to fetch"));

    await store.set("Starters", "false");

    expect(store.get("Starters", "true")).toBe("true");
  });

  it("saves again after a failure", async () => {
    const store = usePreferencesStore();
    await store.refresh();
    put.mockResolvedValueOnce({ error: { error: "boom" }, response: { status: 500 } });
    await store.set("Starters", "false");

    put.mockResolvedValueOnce({ data: {}, response: { status: 200 } });
    await store.set("Starters", "false");

    expect(store.get("Starters", "true")).toBe("false");
    expect(put).toHaveBeenCalledTimes(2);
  });
});
