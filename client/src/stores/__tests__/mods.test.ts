import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { KeptMod, ModDraft, ModsView } from "@/lib/mods/kept";
import type { MachineConnection } from "@/lib/machines";

const api = vi.hoisted(() => ({
  fetchModsSwitch: vi.fn(),
  fetchMods: vi.fn(),
  undoMod: vi.fn(),
  setModOn: vi.fn(),
  setSafeMode: vi.fn(),
  fetchDrafts: vi.fn(),
  setDraftOn: vi.fn(),
  keepDraft: vi.fn(),
  dismissKeepRequest: vi.fn(),
}));
const events = vi.hoisted(() => ({
  onDomainEvent: vi.fn(),
  handlers: new Map<string, (event: { payload: Record<string, unknown> }) => void>(),
}));
const remote = vi.hoisted(() => ({ connection: { id: "remote" } as unknown }));

vi.mock("@/lib/mods/kept-api", async (importActual) => ({
  ...(await importActual<typeof import("@/lib/mods/kept-api")>()),
  ...api,
}));
vi.mock("@/composables/on-domain-event", () => ({ onDomainEvent: events.onDomainEvent }));
vi.mock("@/lib/machine-target", () => ({
  liveTarget: () => ({ key: "home", connection: null, isLive: true }),
}));
vi.mock("@/stores/machines", () => ({
  useMachinesStore: () => ({
    sessionTarget: (sessionId: string) =>
      sessionId === "s-remote"
        ? { key: "remote", connection: remote.connection, isLive: false }
        : { key: "home", connection: null, isLive: true },
  }),
}));

import { ModsRequestError } from "@/lib/mods/kept-api";
import { useModsStore } from "@/stores/mods";
import { usePreferencesStore } from "@/stores/preferences";
import { MODS_PREFERENCE_KEY } from "@/lib/mods";

const mod = (name: string, overrides: Partial<KeptMod> = {}): KeptMod => ({
  name,
  description: null,
  active: 1,
  activeVersion: "1.0.0",
  off: null,
  versions: [],
  ...overrides,
});
const view = (mods: KeptMod[], safeMode = false): ModsView => ({ safeMode, mods });
const draft = (sessionId: string, name: string, overrides: Partial<ModDraft> = {}): ModDraft => ({
  sessionId,
  name,
  description: null,
  version: null,
  off: null,
  kept: null,
  ...overrides,
});

/** Fires a `mods.changed` event as the machine `key` pushes it. */
function emit(key: string, payload: Record<string, unknown>) {
  events.handlers.get(key)?.({ payload });
}

beforeEach(() => {
  setActivePinia(createPinia());
  for (const fn of Object.values(api)) fn.mockReset();
  events.handlers.clear();
  events.onDomainEvent.mockReset();
  events.onDomainEvent.mockImplementation((target: { key: string }, topic, type, handler) => {
    expect([topic, type]).toEqual(["sessions", "mods.changed"]);
    events.handlers.set(target.key, handler);
    return () => events.handlers.delete(target.key);
  });
});

describe("loading", () => {
  it("fills the switch and a session's drafts", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: false });
    api.fetchDrafts.mockResolvedValue([draft("s1", "context-gauge")]);
    const store = useModsStore();
    await Promise.all([store.loadSwitch(), store.loadDrafts("s1")]);
    expect(store.isSwitchedOn).toBe(true);
    expect(store.draftsFor("s1").map((d) => d.name)).toEqual(["context-gauge"]);
  });

  it("follows the user's Mods choice as soon as it's made, and the server's answer until then", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: false, safeMode: false });
    const store = useModsStore();
    const preferences = usePreferencesStore();
    await store.loadSwitch();
    expect(store.isSwitchedOn).toBe(false);

    // Turning Mods on in Features shows the draft cards without a reload.
    preferences.preferences = { [MODS_PREFERENCE_KEY]: "true" };
    expect(store.isSwitchedOn).toBe(true);
    preferences.preferences = { [MODS_PREFERENCE_KEY]: "false" };
    expect(store.isSwitchedOn).toBe(false);
  });

  it("draftsFor is empty for unknown sessions", () => {
    expect(useModsStore().draftsFor("nope")).toEqual([]);
  });

  it("coalesces concurrent loads of the same thing", async () => {
    api.fetchDrafts.mockResolvedValue([]);
    const store = useModsStore();
    await Promise.all([store.loadDrafts("s1"), store.loadDrafts("s1")]);
    expect(api.fetchDrafts).toHaveBeenCalledTimes(1);
    await store.loadDrafts("s1");
    expect(api.fetchDrafts).toHaveBeenCalledTimes(2);
  });

  it("leaves state empty when Mods are switched off (404)", async () => {
    api.fetchDrafts.mockRejectedValue(new ModsRequestError("Mods are off.", 404));
    const store = useModsStore();
    await expect(store.loadDrafts("s1")).resolves.toBeUndefined();
    expect(store.draftsFor("s1")).toEqual([]);
  });
});

describe("setDraftOn", () => {
  it("replaces that draft in place", async () => {
    api.fetchDrafts.mockResolvedValue([draft("s1", "a"), draft("s1", "b")]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    const off = draft("s1", "a", { off: { by: "user", at: "2026-10-10T10:00:00Z" } });
    api.setDraftOn.mockResolvedValue(off);
    await store.setDraftOn("s1", "a", false);
    expect(api.setDraftOn).toHaveBeenCalledWith("s1", "a", false, null);
    expect(store.draftsFor("s1")).toEqual([{ ...off, keepRequest: null }, draft("s1", "b")]);
  });

  it("clears the keep request when turning off, not when turning on", async () => {
    const asked = { note: "keep it", at: "2026-10-10T10:00:00Z" };
    api.fetchDrafts.mockResolvedValue([draft("s1", "a", { keepRequest: asked }), draft("s1", "b", { keepRequest: asked })]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    api.setDraftOn.mockResolvedValue(draft("s1", "a", { keepRequest: asked }));
    await store.setDraftOn("s1", "a", false);
    expect(store.draftsFor("s1")[0]?.keepRequest).toBeNull();
    expect(store.draftsFor("s1")[1]?.keepRequest).toEqual(asked);
    api.setDraftOn.mockResolvedValue(draft("s1", "b", { keepRequest: asked }));
    await store.setDraftOn("s1", "b", true);
    expect(store.draftsFor("s1")[1]?.keepRequest).toEqual(asked);
  });

  it("rethrows the request error", async () => {
    api.setDraftOn.mockRejectedValue(new ModsRequestError("That draft is gone.", 404));
    await expect(useModsStore().setDraftOn("s1", "x", false)).rejects.toMatchObject({ message: "That draft is gone.", status: 404 });
  });
});

describe("keep and dismissKeepRequest", () => {
  const asked = { note: "keep it", at: "2026-10-10T10:00:00Z" };

  it("keep reloads that session's drafts, and rethrows a refusal leaving the request", async () => {
    api.fetchDrafts.mockResolvedValueOnce([draft("s1", "a", { keepRequest: asked })]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    api.keepDraft.mockRejectedValueOnce(new ModsRequestError("The check found a problem.", 400));
    await expect(store.keep("s1", "a", "")).rejects.toMatchObject({ status: 400 });
    expect(store.draftsFor("s1")[0]?.keepRequest).toEqual(asked);
    api.keepDraft.mockResolvedValue({ name: "a" });
    api.fetchDrafts.mockResolvedValueOnce([draft("s1", "a", { kept: 1 })]);
    await store.keep("s1", "a", "note");
    expect(api.keepDraft).toHaveBeenLastCalledWith("s1", "a", "note", null);
    expect(store.draftsFor("s1")[0]?.kept).toBe(1);
  });

  it("dismissKeepRequest clears the request and reloads", async () => {
    api.fetchDrafts.mockResolvedValueOnce([draft("s1", "a", { keepRequest: asked })]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    api.dismissKeepRequest.mockResolvedValue(undefined);
    api.fetchDrafts.mockResolvedValueOnce([draft("s1", "a")]);
    await store.dismissKeepRequest("s1", "a");
    expect(api.dismissKeepRequest).toHaveBeenCalledWith("s1", "a", null);
    expect(store.draftsFor("s1")[0]?.keepRequest ?? null).toBeNull();
  });
});

describe("mods.changed", () => {
  it("subscribes lazily, once per machine", async () => {
    const store = useModsStore();
    expect(events.onDomainEvent).not.toHaveBeenCalled();
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: false });
    api.fetchDrafts.mockResolvedValue([]);
    await store.loadSwitch();
    await store.loadDrafts("s1");
    expect(events.onDomainEvent).toHaveBeenCalledTimes(1);
    await store.loadDrafts("s-remote");
    expect(events.onDomainEvent).toHaveBeenCalledTimes(2);
  });

  it.each(["draft-written", "keep-requested", "draft-on", "draft-off"])("reloads a tracked session's drafts on %s", async (reason) => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s1");
    emit("home", { reason, sessionId: "other", name: "a" });
    expect(api.fetchDrafts).toHaveBeenCalledTimes(1);
    emit("home", { reason, sessionId: "s1", name: "a" });
    await vi.waitFor(() => expect(api.fetchDrafts).toHaveBeenCalledTimes(2));
  });

  it("reads the switch again when the server says it moved", async () => {
    api.fetchModsSwitch.mockResolvedValueOnce({ on: false, safeMode: false }).mockResolvedValueOnce({ on: true, safeMode: false });
    const store = useModsStore();
    await store.loadSwitch();
    emit("home", { reason: "switch" });
    await vi.waitFor(() => expect(store.isSwitchedOn).toBe(true));
  });

  it("refreshes a remote session's drafts from that machine's stream", async () => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s-remote");
    emit("remote", { reason: "draft-written", sessionId: "s-remote", name: "a" });
    await vi.waitFor(() => expect(api.fetchDrafts).toHaveBeenCalledTimes(2));
    expect(api.fetchDrafts).toHaveBeenLastCalledWith("s-remote", remote.connection as MachineConnection);
  });
});

describe("kept mods", () => {
  it("loads the list, and leaves it empty on a 404", async () => {
    api.fetchMods.mockResolvedValueOnce(view([mod("test-chips")]));
    const store = useModsStore();
    await store.loadKept();
    expect(store.kept?.mods.map((m) => m.name)).toEqual(["test-chips"]);
    api.fetchMods.mockRejectedValueOnce(new ModsRequestError("Mods are off.", 404));
    await store.loadKept();
    expect(store.kept).toBeNull();
  });

  it("rejects with the server's message when the list can't load, so Settings can offer Retry", async () => {
    api.fetchMods.mockRejectedValueOnce(new ModsRequestError("The mods folder can't be read.", 500));
    await expect(useModsStore().loadKept()).rejects.toThrow("The mods folder can't be read.");
  });

  it("undo and setOn replace the mod in the list", async () => {
    api.fetchMods.mockResolvedValue(view([mod("a"), mod("b")]));
    const store = useModsStore();
    await store.loadKept();
    api.undoMod.mockResolvedValue(mod("a", { active: 2 }));
    await store.undo("a");
    expect(api.undoMod).toHaveBeenCalledWith("a", null);
    expect(store.kept?.mods[0]?.active).toBe(2);
    api.setModOn.mockResolvedValue(mod("b", { off: { by: "user", at: "x" } }));
    await store.setOn("b", false);
    expect(api.setModOn).toHaveBeenCalledWith("b", false, null);
    expect(store.kept?.mods[1]?.off?.by).toBe("user");
  });

  it("setSafeMode takes the answer into the list and the switch", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: false });
    const store = useModsStore();
    await store.loadSwitch();
    api.setSafeMode.mockResolvedValue(view([mod("a")], true));
    await store.setSafeMode(true);
    expect(store.kept?.safeMode).toBe(true);
    expect(store.modsSwitch).toEqual({ on: true, safeMode: true });
    expect(store.safeMode).toBe(true);
  });

  it("safe-mode reloads the switch, and kept if loaded", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: false });
    const store = useModsStore();
    await store.loadSwitch();
    emit("home", { reason: "safe-mode" });
    await vi.waitFor(() => expect(api.fetchModsSwitch).toHaveBeenCalledTimes(2));
    expect(api.fetchMods).not.toHaveBeenCalled();
    api.fetchMods.mockResolvedValue(view([]));
    await store.loadKept();
    emit("home", { reason: "safe-mode" });
    await vi.waitFor(() => expect(api.fetchMods).toHaveBeenCalledTimes(2));
  });

  it.each(["kept", "version", "undone", "on", "off", "strikes"])("reloads kept on %s, only once loaded", async (reason) => {
    api.fetchDrafts.mockResolvedValue([]);
    api.fetchMods.mockResolvedValue(view([]));
    const store = useModsStore();
    await store.loadDrafts("s1");
    emit("home", { reason, name: "a" });
    expect(api.fetchMods).not.toHaveBeenCalled();
    await store.loadKept();
    emit("home", { reason, name: "a" });
    await vi.waitFor(() => expect(api.fetchMods).toHaveBeenCalledTimes(2));
  });

});
