import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { apiFetch } from "@/lib/api-client";
import { liveTarget } from "@/lib/machine-target";
import { MODS_RUNTIME_EVENT, type ModsRuntimeJob, type ModsRuntimeView } from "@/lib/mods-runtime";

const RUNTIME = "/api/features/mods/runtime";

/** The server's reason for refusing: its `error` field, else the status. */
async function errorText(response: Response): Promise<string> {
  const body = (await response.json().catch(() => null)) as { error?: unknown } | null;
  return typeof body?.error === "string" && body.error.trim() ? body.error : `Fleet answered ${response.status}.`;
}

/**
 * The Bun that mods run on and the install running now or the last one. The server keeps the install going whether
 * or not Settings is open, so this only mirrors it: `load()` on demand and the pushed `mods.runtime` events.
 */
export const useModsRuntimeStore = defineStore("mods-runtime", () => {
  const view = shallowRef<ModsRuntimeView | null>(null);
  /** Whether the row is asking the user to confirm the install; the switch stays off meanwhile. */
  const confirming = shallowRef(false);
  let stopListening: (() => void) | null = null;

  async function load(): Promise<void> {
    try {
      const response = await apiFetch(RUNTIME);
      if (response.ok) view.value = (await response.json()) as ModsRuntimeView;
    } catch {
      // Keep what's shown: the next event or visit asks again.
    }
  }

  /** POSTs a request that answers with the view. Returns the server's message when it refuses, else null. */
  async function post(action: "install" | "cancel"): Promise<string | null> {
    try {
      const response = await apiFetch(`${RUNTIME}/${action}`, { method: "POST" });
      if (!response.ok) return await errorText(response);
      view.value = (await response.json()) as ModsRuntimeView;
      return null;
    } catch {
      return "Fleet couldn't be reached.";
    }
  }

  /** Progress changes the job in place; any other change (a phase, the user's Bun, a release) is read again. */
  function applyEvent(payload: { job?: ModsRuntimeJob | null } | undefined): void {
    const next = payload?.job ?? null;
    if (next && view.value) view.value = { ...view.value, job: next };
    if (next?.phase !== "downloading") void load();
  }

  /** Follows the pushed events, once. */
  function listen(): void {
    stopListening ??= onDomainEvent(liveTarget(), "sessions", MODS_RUNTIME_EVENT, (event) => applyEvent(event.payload));
  }

  return {
    view,
    confirming,
    load,
    /** Turns Mods on and starts, or joins, the install of Fleet's own Bun. */
    install: () => post("install"),
    cancel: () => post("cancel"),
    applyEvent,
    listen,
  };
});
