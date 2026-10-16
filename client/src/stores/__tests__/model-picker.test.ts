import { beforeEach, describe, expect, it, vi } from "vitest";
import { modelPickerKey, useModelPickerStore } from "@/stores/model-picker";
import { usePreferencesStore } from "@/stores/preferences";

vi.mock("@/api/client", () => ({ api: { GET: vi.fn(), PUT: vi.fn() } }));

function setup(raw?: string) {
  const preferences = usePreferencesStore();
  vi.spyOn(preferences, "ensureLoaded").mockImplementation(() => {});
  const set = vi.spyOn(preferences, "set").mockImplementation(async (key, value) => {
    preferences.preferences = { ...preferences.preferences, [key]: value };
  });
  preferences.hasFetched = true;
  if (raw !== undefined) preferences.preferences = { modelPicker: raw };
  return { store: useModelPickerStore(), set, saved: () => JSON.parse(set.mock.calls.at(-1)![1]) as Record<string, string[]> };
}

describe("model picker store", () => {
  beforeEach(() => vi.restoreAllMocks());

  it("reads bad JSON and missing arrays as empty", () => {
    expect(setup("{nope").store.favourites).toEqual([]);
    expect(setup('{"favourites":"x"}').store.recent).toEqual([]);
  });

  it("appends a favourite and keeps hidden and unknown keys", async () => {
    const { store, saved } = setup(JSON.stringify({ favourites: ["old:gone/x"], hidden: ["h:p/m"], recent: [] }));
    await store.toggleFavourite(modelPickerKey("opencode", "p", "m"));
    expect(saved()).toEqual({ favourites: ["old:gone/x", "opencode:p/m"], hidden: ["h:p/m"], recent: [] });
    expect(store.isFavourite("opencode:p/m")).toBe(true);
    await store.toggleFavourite("opencode:p/m");
    expect(saved().favourites).toEqual(["old:gone/x"]);
  });

  it("moves a recent to the front, caps five per harness and leaves other harnesses alone", async () => {
    const { store, saved } = setup(JSON.stringify({ favourites: [], hidden: [], recent: ["claude:p/c1", "o:p/1", "o:p/2", "o:p/3", "o:p/4", "o:p/5"] }));
    await store.recordRecent("o:p/3");
    expect(saved().recent).toEqual(["o:p/3", "o:p/1", "o:p/2", "o:p/4", "o:p/5", "claude:p/c1"]);
    await store.recordRecent("o:p/6");
    expect(saved().recent).toEqual(["o:p/6", "o:p/3", "o:p/1", "o:p/2", "o:p/4", "claude:p/c1"]);
  });

  it("doesn't write when nothing changed", async () => {
    const { store, set } = setup(JSON.stringify({ favourites: [], hidden: [], recent: ["o:p/1"] }));
    await store.recordRecent("o:p/1");
    expect(set).not.toHaveBeenCalled();
  });
});
