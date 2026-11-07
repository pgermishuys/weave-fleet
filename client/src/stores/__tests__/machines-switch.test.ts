import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import {
  HOME_MACHINE_KEY,
  LIVE_MACHINES_PREFERENCE_KEY,
  MACHINE_SWITCH_PREFERENCE_KEY,
  loadLiveMachinesHint,
  saveMachines,
  setActiveMachine,
  type MachineConnection,
} from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * "Switch machines without reloading" (Settings → Labs): another machine's session opens in place like with
 * "Keep every machine live", but opens no live feeds, and the machine you're on follows the open session.
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

describe("switching machines without reloading", () => {
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

  it("opens in place with its own switch on and live feeds off, and not with both off", () => {
    const store = useMachinesStore();
    setPreferences({ [MACHINE_SWITCH_PREFERENCE_KEY]: "true", [LIVE_MACHINES_PREFERENCE_KEY]: "false" });
    expect(store.opensInPlace).toBe(true);

    setPreferences({ [MACHINE_SWITCH_PREFERENCE_KEY]: "false", [LIVE_MACHINES_PREFERENCE_KEY]: "false" });
    expect(store.opensInPlace).toBe(false);
  });

  it("remembers that it opens in place for the next startup, so a reload of another machine's session stays in place", () => {
    const store = useMachinesStore();
    setPreferences({ [MACHINE_SWITCH_PREFERENCE_KEY]: "true" });
    expect(store.opensInPlace).toBe(true);

    return Promise.resolve().then(() => expect(loadLiveMachinesHint()).toBe(true));
  });

  it("is on the live machine until a session on another machine is open, and stays on it afterwards", () => {
    const store = useMachinesStore();
    setPreferences({ [MACHINE_SWITCH_PREFERENCE_KEY]: "true" });
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);

    store.setCurrent(mini.id);
    expect(store.currentKey).toBe(mini.id);
    expect(store.current.name).toBe("mini");

    store.setCurrent(HOME_MACHINE_KEY);
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);
  });

  it("is the live machine whatever was opened while the switch is off, or for a machine that's gone", () => {
    const store = useMachinesStore();
    setPreferences({ [MACHINE_SWITCH_PREFERENCE_KEY]: "false" });
    store.setCurrent(mini.id);
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);

    setPreferences({ [MACHINE_SWITCH_PREFERENCE_KEY]: "true" });
    store.setCurrent("cccccccccccccccccccccccccccccccc");
    expect(store.currentKey).toBe(HOME_MACHINE_KEY);
  });

  it("gives a target for any listed machine, the live one for one that isn't", () => {
    const store = useMachinesStore();
    expect(store.targetOfKey(mini.id)).toMatchObject({ key: mini.id, isLive: false, connection: mini });
    expect(store.targetOfKey(HOME_MACHINE_KEY)).toMatchObject({ key: HOME_MACHINE_KEY, isLive: true });
    expect(store.targetOfKey("dddddddddddddddddddddddddddddddd")).toMatchObject({ isLive: true });
  });
});
