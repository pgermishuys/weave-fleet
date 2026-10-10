import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { KeptMod, ModDraft, ModsView } from "@/lib/mods/kept";
import type { MachineConnection } from "@/lib/machines";

const api = vi.hoisted(() => ({
  fetchModsSwitch: vi.fn(),
  fetchMods: vi.fn(),
  fetchDrafts: vi.fn(),
  keepDraft: vi.fn(),
  dismissKeepRequest: vi.fn(),
  setDraftOn: vi.fn(),
  activateModVersion: vi.fn(),
  undoMod: vi.fn(),
  setModOn: vi.fn(),
  setSafeMode: vi.fn(),
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

const mod = (name: string, overrides: Partial<KeptMod> = {}): KeptMod => ({
  name,
  description: null,
  active: 1,
  activeVersion: "1.0.0",
  off: null,
  versions: [],
  ...overrides,
});
const draft = (sessionId: string, name: string, overrides: Partial<ModDraft> = {}): ModDraft => ({
  sessionId,
  name,
  description: null,
  version: null,
  off: null,
  kept: null,
  ...overrides,
});
const view = (mods: KeptMod[], safeMode = false): ModsView => ({ safeMode, mods });

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
  it("fills the switch, the kept list and a session's drafts", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: false });
    api.fetchMods.mockResolvedValue(view([mod("test-chips")]));
    api.fetchDrafts.mockResolvedValue([draft("s1", "context-gauge")]);
    const store = useModsStore();
    await Promise.all([store.loadSwitch(), store.loadKept(), store.loadDrafts("s1")]);
    expect(store.isSwitchedOn).toBe(true);
    expect(store.kept?.mods.map((m) => m.name)).toEqual(["test-chips"]);
    expect(store.draftsFor("s1").map((d) => d.name)).toEqual(["context-gauge"]);
  });

  it("draftsFor is empty for unknown sessions", () => {
    expect(useModsStore().draftsFor("nope")).toEqual([]);
  });

  it("sends a session's drafts to its machine", async () => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s-remote");
    expect(api.fetchDrafts).toHaveBeenCalledWith("s-remote", remote.connection);
  });

  it("coalesces concurrent loads of the same thing", async () => {
    api.fetchMods.mockResolvedValue(view([]));
    const store = useModsStore();
    await Promise.all([store.loadKept(), store.loadKept()]);
    expect(api.fetchMods).toHaveBeenCalledTimes(1);
    await store.loadKept();
    expect(api.fetchMods).toHaveBeenCalledTimes(2);
  });

  it("leaves state empty when Mods are switched off (404)", async () => {
    api.fetchMods.mockRejectedValue(new ModsRequestError("Mods are off.", 404));
    api.fetchDrafts.mockRejectedValue(new ModsRequestError("Mods are off.", 404));
    const store = useModsStore();
    await expect(store.loadKept()).resolves.toBeUndefined();
    await expect(store.loadDrafts("s1")).resolves.toBeUndefined();
    expect(store.kept).toBeNull();
    expect(store.draftsFor("s1")).toEqual([]);
  });
});

describe("safeMode", () => {
  it("prefers the kept view, then the switch", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: true });
    const store = useModsStore();
    expect(store.safeMode).toBe(false);
    await store.loadSwitch();
    expect(store.safeMode).toBe(true);
    api.fetchMods.mockResolvedValue(view([], false));
    await store.loadKept();
    expect(store.safeMode).toBe(false);
  });
});

describe("actions", () => {
  it("keep reloads the session's drafts and the kept list when loaded", async () => {
    api.fetchMods.mockResolvedValue(view([]));
    api.fetchDrafts.mockResolvedValue([draft("s1", "test-chips")]);
    const store = useModsStore();
    await store.loadKept();
    await store.loadDrafts("s1");
    api.keepDraft.mockResolvedValue(mod("test-chips"));
    api.fetchMods.mockResolvedValue(view([mod("test-chips")]));
    api.fetchDrafts.mockResolvedValue([draft("s1", "test-chips", { kept: 1 })]);
    await store.keep("s1", "test-chips", "show names");
    expect(api.keepDraft).toHaveBeenCalledWith("s1", "test-chips", "show names", null);
    expect(store.draftsFor("s1")[0]?.kept).toBe(1);
    expect(store.kept?.mods).toHaveLength(1);
  });

  it("setDraftOn replaces that draft in place", async () => {
    api.fetchDrafts.mockResolvedValue([draft("s1", "a"), draft("s1", "b")]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    const off = draft("s1", "a", { off: { by: "user", at: "2026-10-10T10:00:00Z" } });
    api.setDraftOn.mockResolvedValue(off);
    await store.setDraftOn("s1", "a", false);
    expect(api.setDraftOn).toHaveBeenCalledWith("s1", "a", false, null);
    expect(store.draftsFor("s1")).toEqual([off, draft("s1", "b")]);
  });

  it("dismissKeepRequest clears that draft's request and reloads the drafts", async () => {
    const asked = { note: "keep it", at: "2026-10-10T10:00:00Z" };
    api.fetchDrafts.mockResolvedValue([draft("s1", "a", { keepRequest: asked })]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    api.dismissKeepRequest.mockResolvedValue(undefined);
    api.fetchDrafts.mockResolvedValue([draft("s1", "a", { keepRequest: null })]);
    await store.dismissKeepRequest("s1", "a");
    expect(api.dismissKeepRequest).toHaveBeenCalledWith("s1", "a", null);
    expect(store.draftsFor("s1")[0]?.keepRequest).toBeNull();
    expect(api.fetchDrafts).toHaveBeenCalledTimes(2);
  });

  it("keep and turning a draft off clear its keep request locally", async () => {
    const asked = { note: "keep it", at: "2026-10-10T10:00:00Z" };
    api.fetchDrafts.mockResolvedValue([draft("s1", "a", { keepRequest: asked }), draft("s1", "b", { keepRequest: asked })]);
    const store = useModsStore();
    await store.loadDrafts("s1");
    api.setDraftOn.mockResolvedValue(draft("s1", "a", { keepRequest: asked }));
    await store.setDraftOn("s1", "a", false);
    expect(store.draftsFor("s1")[0]?.keepRequest).toBeNull();
    expect(store.draftsFor("s1")[1]?.keepRequest).toEqual(asked);
    api.keepDraft.mockResolvedValue(mod("b"));
    api.fetchDrafts.mockImplementation(() => new Promise(() => {}));
    void store.keep("s1", "b", "");
    await vi.waitFor(() => expect(store.draftsFor("s1")[1]?.keepRequest).toBeNull());
  });

  it("activateVersion, undo and setOn replace the mod in kept", async () => {
    api.fetchMods.mockResolvedValue(view([mod("a"), mod("b")]));
    const store = useModsStore();
    await store.loadKept();
    api.activateModVersion.mockResolvedValue(mod("a", { active: 2 }));
    await store.activateVersion("a", 2);
    expect(api.activateModVersion).toHaveBeenCalledWith("a", 2, null);
    expect(store.kept?.mods[0]?.active).toBe(2);
    api.undoMod.mockResolvedValue(mod("a", { active: 1 }));
    await store.undo("a");
    expect(store.kept?.mods[0]?.active).toBe(1);
    api.setModOn.mockResolvedValue(mod("b", { off: { by: "user", at: "x" } }));
    await store.setOn("b", false);
    expect(api.setModOn).toHaveBeenCalledWith("b", false, null);
    expect(store.kept?.mods.map((m) => m.name)).toEqual(["a", "b"]);
    expect(store.kept?.mods[1]?.off?.by).toBe("user");
  });

  it("setSafeMode takes the response into kept and the switch", async () => {
    api.fetchModsSwitch.mockResolvedValue({ on: true, safeMode: false });
    const store = useModsStore();
    await store.loadSwitch();
    api.setSafeMode.mockResolvedValue(view([mod("a")], true));
    await store.setSafeMode(true);
    expect(store.kept?.safeMode).toBe(true);
    expect(store.modsSwitch).toEqual({ on: true, safeMode: true });
    expect(store.safeMode).toBe(true);
  });

  it("rethrows the request error", async () => {
    api.undoMod.mockRejectedValue(new ModsRequestError("No such mod.", 404));
    await expect(useModsStore().undo("x")).rejects.toMatchObject({ message: "No such mod.", status: 404 });
  });
});

describe("mods.changed", () => {
  it("subscribes lazily, once per machine", async () => {
    const store = useModsStore();
    expect(events.onDomainEvent).not.toHaveBeenCalled();
    api.fetchMods.mockResolvedValue(view([]));
    api.fetchDrafts.mockResolvedValue([]);
    await store.loadKept();
    await store.loadKept();
    await store.loadDrafts("s1");
    expect(events.onDomainEvent).toHaveBeenCalledTimes(1);
    await store.loadDrafts("s-remote");
    expect(events.onDomainEvent).toHaveBeenCalledTimes(2);
  });

  it("reloads the switch (and kept, if loaded) on safe-mode", async () => {
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

  it.each(["kept", "version", "undone", "on", "off", "strikes"])("reloads kept on %s", async (reason) => {
    api.fetchMods.mockResolvedValue(view([]));
    await useModsStore().loadKept();
    emit("home", { reason, name: "a" });
    await vi.waitFor(() => expect(api.fetchMods).toHaveBeenCalledTimes(2));
  });

  it.each(["draft-written", "keep-requested"])("reloads a tracked session's drafts on %s", async (reason) => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s1");
    emit("home", { reason, sessionId: "other", name: "a" });
    expect(api.fetchDrafts).toHaveBeenCalledTimes(1);
    emit("home", { reason, sessionId: "s1", name: "a" });
    await vi.waitFor(() => expect(api.fetchDrafts).toHaveBeenCalledTimes(2));
    expect(api.fetchMods).not.toHaveBeenCalled();
  });

  it("ignores kept changes until kept was loaded", async () => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s1");
    emit("home", { reason: "kept", name: "a" });
    await Promise.resolve();
    expect(api.fetchMods).not.toHaveBeenCalled();
  });

  it("reloads the drafts of a tracked session only", async () => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s1");
    emit("home", { reason: "draft-on", sessionId: "other", name: "a" });
    expect(api.fetchDrafts).toHaveBeenCalledTimes(1);
    emit("home", { reason: "draft-on", sessionId: "s1", name: "a" });
    await vi.waitFor(() => expect(api.fetchDrafts).toHaveBeenCalledTimes(2));
  });

  it("refreshes a remote session's drafts from that machine's stream", async () => {
    api.fetchDrafts.mockResolvedValue([]);
    await useModsStore().loadDrafts("s-remote");
    emit("remote", { reason: "draft-off", sessionId: "s-remote", name: "a" });
    await vi.waitFor(() => expect(api.fetchDrafts).toHaveBeenCalledTimes(2));
    expect(api.fetchDrafts).toHaveBeenLastCalledWith("s-remote", remote.connection as MachineConnection);
  });
});
