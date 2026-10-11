import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import type { ModDraft, ModsSwitch } from "@/lib/mods/kept";
import * as modsApi from "@/lib/mods/kept-api";
import { ModsRequestError } from "@/lib/mods/kept-api";
import { MODS_PREFERENCE_KEY } from "@/lib/mods";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * The Mods switch and each session's drafts, for the draft cards. It follows `mods.changed` itself, refetching a
 * tracked session's drafts, so components read it and call its actions. A session's drafts belong to the machine the
 * session is on.
 */
export const useModsStore = defineStore("mods", () => {
  const modsSwitch = shallowRef<ModsSwitch | null>(null);
  const drafts = shallowRef<Record<string, ModDraft[]>>({});
  const stops = new Map<string, () => void>();
  const inflight = new Map<string, Promise<void>>();

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
    stops.set(target.key, onDomainEvent(target, "sessions", "mods.changed", ({ payload }) => {
      const sessionId = payload.sessionId;
      if (sessionId && sessionId in drafts.value) void loadDrafts(sessionId);
      // The Mods switch moved (on this machine, or from another tab).
      if (payload.reason === "switch" && target.key === liveTarget().key) void loadSwitch();
    }));
  }

  function loadSwitch(): Promise<void> {
    const target = liveTarget();
    listen(target);
    return once("switch", async () => {
      modsSwitch.value = await modsApi.fetchModsSwitch(target.connection);
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

  async function setDraftOn(sessionId: string, name: string, on: boolean): Promise<ModDraft> {
    const draft = await modsApi.setDraftOn(sessionId, name, on, machineOf(sessionId).connection);
    const list = drafts.value[sessionId];
    if (list) {
      const next = on ? draft : { ...draft, keepRequest: null };
      drafts.value = { ...drafts.value, [sessionId]: list.map((d) => (d.name === name ? next : d)) };
    }
    return draft;
  }

  return { modsSwitch, drafts, isSwitchedOn, draftsFor, loadSwitch, loadDrafts, setDraftOn };
});
