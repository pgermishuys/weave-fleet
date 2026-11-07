import { nextTick } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import {
  HOME_MACHINE_KEY,
  LIVE_MACHINES_PREFERENCE_KEY,
  saveMachines,
  setActiveMachine,
  type MachineConnection,
} from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

const { feedHubFor, hubs } = vi.hoisted(() => {
  const hubs: { key: string; start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn> }[] = [];
  return {
    hubs,
    feedHubFor: vi.fn((target: { key: string }) => {
      const hub = { key: target.key, start: vi.fn(async () => {}), stop: vi.fn(async () => {}) };
      hubs.push(hub);
      return hub;
    }),
  };
});
vi.mock("@/composables/use-signalr-socket", async (original) => ({
  ...await original<typeof import("@/composables/use-signalr-socket")>(),
  feedHubFor,
}));

/**
 * Another machine's session opens in place whenever machines are listed (no switch), the machine you're on follows
 * the open session, and each other machine's event hub is held idle unless live feeds hold it.
 */

const mini: MachineConnection = {
  id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  name: "mini",
  baseUrl: "http://mini.example.test:2113",
  token: "mini-token-0123456789",
  os: "macos",
  addedAt: "2026-10-08T00:00:00.000Z",
};

function setPreferences(values: Record<string, string>): void {
  const preferences = usePreferencesStore();
  preferences.hasFetched = true;
  preferences.preferences = values;
}

describe("switching machines in place", () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    saveMachines([mini]);
    vi.stubGlobal("fetch", vi.fn(async () => Response.json([])));
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it("opens in place whenever machines are listed, with no preference set", () => {
    expect(useMachinesStore().opensInPlace).toBe(true);
    saveMachines([]);
    setActivePinia(createPinia());
    expect(useMachinesStore().opensInPlace).toBe(false);
  });

  it("starts on the machine this tab was last on, else the live one, and ignores one that's gone", () => {
    sessionStorage.setItem("weave:current-machine", JSON.stringify(mini.id));
    expect(useMachinesStore().currentKey).toBe(mini.id);

    sessionStorage.setItem("weave:current-machine", JSON.stringify("cccccccccccccccccccccccccccccccc"));
    setActivePinia(createPinia());
    expect(useMachinesStore().currentKey).toBe(HOME_MACHINE_KEY);
  });

  it("is on the live machine until a session on another machine is open, and stays on it afterwards", () => {
    const store = useMachinesStore();
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);

    store.setCurrent(mini.id);
    expect(store.currentKey).toBe(mini.id);
    expect(store.current.name).toBe("mini");

    store.setCurrent(HOME_MACHINE_KEY);
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);
  });

  it("is the live machine for a machine that's gone", () => {
    const store = useMachinesStore();
    store.setCurrent("cccccccccccccccccccccccccccccccc");
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);
  });

  it("gives a target for any listed machine, the live one for one that isn't", () => {
    const store = useMachinesStore();
    expect(store.targetOfKey(mini.id)).toMatchObject({ key: mini.id, isLive: false, connection: mini });
    expect(store.targetOfKey(HOME_MACHINE_KEY)).toMatchObject({ key: HOME_MACHINE_KEY, isLive: true });
    expect(store.targetOfKey("dddddddddddddddddddddddddddddddd")).toMatchObject({ isLive: true });
  });

  it("holds each other machine's event hub open and idle unless live feeds are on, and lets go when they come on", async () => {
    hubs.length = 0;
    feedHubFor.mockClear();
    const store = useMachinesStore();
    const stop = store.startPolling();
    await nextTick();

    expect(hubs.map((hub) => hub.key)).toEqual([mini.id]);
    expect(hubs[0].start).toHaveBeenCalledTimes(1);

    setPreferences({ [LIVE_MACHINES_PREFERENCE_KEY]: "true" });
    await nextTick();
    expect(hubs[0].stop).toHaveBeenCalledTimes(1);
    stop();
  });

  it("holds no hub of its own with live feeds on: the feeds hold them", async () => {
    hubs.length = 0;
    setPreferences({ [LIVE_MACHINES_PREFERENCE_KEY]: "true" });
    const store = useMachinesStore();
    const stop = store.startPolling();
    await nextTick();

    expect(hubs).toHaveLength(0);
    stop();
  });

  it("keeps the hubs held with nothing polling, as when Settings replaces the sidebar", async () => {
    hubs.length = 0;
    const store = useMachinesStore();
    const stopSidebar = store.startPolling();
    await nextTick();
    stopSidebar();
    await nextTick();

    expect(hubs.map((hub) => hub.key)).toEqual([mini.id]);
    expect(hubs[0].stop).not.toHaveBeenCalled();
    const stopSettings = store.startPolling();
    await nextTick();
    expect(hubs).toHaveLength(1);
    stopSettings();
  });

  it("holds them before anything polls, and lets go when live feeds come on with nothing polling", async () => {
    hubs.length = 0;
    const store = useMachinesStore();
    await nextTick();
    expect(hubs.map((hub) => hub.key)).toEqual([mini.id]);
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);

    setPreferences({ [LIVE_MACHINES_PREFERENCE_KEY]: "true" });
    await nextTick();
    expect(hubs[0].stop).toHaveBeenCalledTimes(1);
  });

  it("holds nothing with no machines listed", async () => {
    hubs.length = 0;
    saveMachines([]);
    setActivePinia(createPinia());
    useMachinesStore().startPolling()();
    await nextTick();
    expect(hubs).toHaveLength(0);
  });
});
