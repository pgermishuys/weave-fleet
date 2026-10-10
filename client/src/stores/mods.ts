import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import type { KeptMod, ModDraft, ModsSwitch, ModsView } from "@/lib/mods/kept";
import * as modsApi from "@/lib/mods/kept-api";
import { ModsRequestError } from "@/lib/mods/kept-api";
import { MODS_PREFERENCE_KEY } from "@/lib/mods";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/** Why a kept mod (rather than a draft or safe mode) changed. */
const KEPT_REASONS = new Set(["kept", "version", "undone", "on", "off", "strikes"]);

/**
 * Kept mods, "Start without mods" and each session's drafts, for Settings → Mods and the draft cards. It follows
 * `mods.changed` itself, refetching what changed, so components read it and call its actions. Kept mods and safe mode
 * belong to the live machine; a session's drafts to the machine the session is on.
 */
export const useModsStore = defineStore("mods", () => {
  const modsSwitch = shallowRef<ModsSwitch | null>(null);
  const kept = shallowRef<ModsView | null>(null);
  const drafts = shallowRef<Record<string, ModDraft[]>>({});
  const stops = new Map<string, () => void>();
  const inflight = new Map<string, Promise<void>>();

  const safeMode = computed(() => kept.value?.safeMode ?? modsSwitch.value?.safeMode ?? false);
  const preferences = usePreferencesStore();
  // The user's choice in Features wins as soon as it's made (no event says the switch moved); until they choose, the
  // server's answer, which falls back to Fleet's own option.
  const isSwitchedOn = computed(() => {
    const chosen = preferences.get(MODS_PREFERENCE_KEY, "");
    return chosen === "" ? modsSwitch.value?.on ?? false : chosen === "true";
  });

  function draftsFor(sessionId: string): ModDraft[] {
    return drafts.value[sessionId] ?? [];
  }

  const machineOf = (sessionId: string): MachineTarget => useMachinesStore().sessionTarget(sessionId);

  /** One fetch per `key` at a time; load failures leave what's shown (a 404 means Mods are off: nothing to show). */
  function once(key: string, run: () => Promise<void>): Promise<void> {
    let pending = inflight.get(key);
    if (!pending) {
      pending = run()
        .catch(() => undefined)
        .finally(() => inflight.delete(key));
      inflight.set(key, pending);
    }
    return pending;
  }

  function listen(target: MachineTarget): void {
    if (stops.has(target.key)) return;
    const key = target.key;
    stops.set(key, onDomainEvent(target, "sessions", "mods.changed", ({ payload }) => {
      const sessionId = payload.sessionId;
      if (sessionId && sessionId in drafts.value) void loadDrafts(sessionId);
      // Kept mods and safe mode are the live machine's; another machine's stream only speaks for its sessions' drafts.
      if (key !== liveTarget().key) return;
      if (payload.reason === "safe-mode") {
        void loadSwitch();
        if (kept.value) void loadKept();
      } else if (KEPT_REASONS.has(payload.reason) && kept.value) {
        void loadKept();
      }
    }));
  }

  function loadSwitch(): Promise<void> {
    const target = liveTarget();
    listen(target);
    return once("switch", async () => {
      modsSwitch.value = await modsApi.fetchModsSwitch(target.connection);
    });
  }

  function loadKept(): Promise<void> {
    const target = liveTarget();
    listen(target);
    return once("kept", async () => {
      try {
        kept.value = await modsApi.fetchMods(target.connection);
      } catch (error) {
        if (error instanceof ModsRequestError && error.status === 404) kept.value = null;
        throw error;
      }
    });
  }

  function loadDrafts(sessionId: string): Promise<void> {
    const target = machineOf(sessionId);
    listen(target);
    return once(`drafts:${sessionId}`, async () => {
      try {
        drafts.value = { ...drafts.value, [sessionId]: await modsApi.fetchDrafts(sessionId, target.connection) };
      } catch (error) {
        // Tracked, so a later `mods.changed` for the session refetches.
        if (error instanceof ModsRequestError && error.status === 404) drafts.value = { ...drafts.value, [sessionId]: [] };
        throw error;
      }
    });
  }

  function replaceDraft(sessionId: string, name: string, change: (draft: ModDraft) => ModDraft): void {
    const list = drafts.value[sessionId];
    if (!list) return;
    drafts.value = { ...drafts.value, [sessionId]: list.map((d) => (d.name === name ? change(d) : d)) };
  }

  function replaceMod(mod: KeptMod): void {
    if (!kept.value) return;
    const has = kept.value.mods.some((m) => m.name === mod.name);
    kept.value = {
      ...kept.value,
      mods: has ? kept.value.mods.map((m) => (m.name === mod.name ? mod : m)) : [...kept.value.mods, mod],
    };
  }

  const withoutKeepRequest = (draft: ModDraft): ModDraft => ({ ...draft, keepRequest: null });

  async function keep(sessionId: string, name: string, note: string): Promise<KeptMod> {
    const mod = await modsApi.keepDraft(sessionId, name, note, machineOf(sessionId).connection);
    replaceDraft(sessionId, name, withoutKeepRequest);
    await Promise.all([loadDrafts(sessionId), kept.value ? loadKept() : undefined]);
    return mod;
  }

  async function setDraftOn(sessionId: string, name: string, on: boolean): Promise<ModDraft> {
    const draft = await modsApi.setDraftOn(sessionId, name, on, machineOf(sessionId).connection);
    replaceDraft(sessionId, name, () => (on ? draft : withoutKeepRequest(draft)));
    return draft;
  }

  async function dismissKeepRequest(sessionId: string, name: string): Promise<void> {
    await modsApi.dismissKeepRequest(sessionId, name, machineOf(sessionId).connection);
    replaceDraft(sessionId, name, withoutKeepRequest);
    await loadDrafts(sessionId);
  }

  async function activateVersion(name: string, number: number): Promise<KeptMod> {
    const mod = await modsApi.activateModVersion(name, number, liveTarget().connection);
    replaceMod(mod);
    return mod;
  }

  async function undo(name: string): Promise<KeptMod> {
    const mod = await modsApi.undoMod(name, liveTarget().connection);
    replaceMod(mod);
    return mod;
  }

  async function setOn(name: string, on: boolean): Promise<KeptMod> {
    const mod = await modsApi.setModOn(name, on, liveTarget().connection);
    replaceMod(mod);
    return mod;
  }

  async function setSafeMode(on: boolean): Promise<ModsView> {
    const view = await modsApi.setSafeMode(on, liveTarget().connection);
    kept.value = view;
    if (modsSwitch.value) modsSwitch.value = { ...modsSwitch.value, safeMode: view.safeMode };
    return view;
  }

  return {
    modsSwitch,
    kept,
    drafts,
    safeMode,
    isSwitchedOn,
    draftsFor,
    loadSwitch,
    loadKept,
    loadDrafts,
    keep,
    setDraftOn,
    dismissKeepRequest,
    activateVersion,
    undo,
    setOn,
    setSafeMode,
  };
});
