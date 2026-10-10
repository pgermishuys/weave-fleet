import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { apiFetch } from "@/lib/api-client";
import { liveTarget } from "@/lib/machine-target";
import { jobRunning, MODS_RUNTIME_EVENT, type BunCandidate, type ModsRuntimeJob, type ModsRuntimeView } from "@/lib/mods-runtime";

const RUNTIME = "/api/features/mods/runtime";

async function errorText(response: Response): Promise<string> {
  const text = await response.text().catch(() => "");
  try {
    const body = JSON.parse(text) as { error?: unknown; detail?: unknown; title?: unknown };
    for (const field of [body.error, body.detail, body.title]) {
      if (typeof field === "string" && field.trim()) return field;
    }
  } catch {
    if (text.trim()) return text.trim();
  }
  return `Fleet answered ${response.status}.`;
}

/**
 * The Bun that mods run on: where it is, the install running now or the last one, and what the user chose. The
 * server keeps the install going whether or not Settings is open, so this only mirrors it: `load()` on demand and
 * the pushed `mods.runtime` events. Looking for a Bun on the computer (`findBuns`) can take seconds, so it runs only
 * when the user asks to turn Mods on.
 */
export const useModsRuntimeStore = defineStore("mods-runtime", () => {
  const view = shallowRef<ModsRuntimeView | null>(null);
  const isLoaded = shallowRef(false);
  let stopListening: (() => void) | null = null;

  const job = computed(() => view.value?.job ?? null);
  const isInstalling = computed(() => jobRunning(job.value));

  async function load(): Promise<void> {
    try {
      const response = await apiFetch(RUNTIME);
      if (response.ok) view.value = (await response.json()) as ModsRuntimeView;
    } catch {
      // Keep what's shown: the next event or visit asks again.
    } finally {
      isLoaded.value = true;
    }
  }

  /** Sends a request that answers with the view. Returns the server's message when it refuses, else null. */
  async function send(path: string, method: "POST" | "PUT", body?: unknown): Promise<string | null> {
    try {
      const response = await apiFetch(path, {
        method,
        ...(body === undefined ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }),
      });
      if (!response.ok) return await errorText(response);
      view.value = (await response.json()) as ModsRuntimeView;
      return null;
    } catch {
      return "Fleet couldn't be reached.";
    }
  }

  /** Turns Mods on and starts, or joins, the install of Fleet's own Bun. */
  function install(): Promise<string | null> {
    return send(`${RUNTIME}/install`, "POST");
  }

  function cancel(): Promise<string | null> {
    return send(`${RUNTIME}/cancel`, "POST");
  }

  /** Sets the Bun the user installed (which turns Mods on), or clears it with null. */
  function setBunPath(path: string | null): Promise<string | null> {
    return send(`${RUNTIME}/bun-path`, "PUT", { path });
  }

  /** The Bun versions on this computer. Slow, so only when the user flips the switch on. */
  async function findBuns(): Promise<BunCandidate[]> {
    try {
      const response = await apiFetch(`${RUNTIME}/found`);
      if (!response.ok) return [];
      const body = (await response.json()) as { candidates?: BunCandidate[] };
      return body.candidates ?? [];
    } catch {
      return [];
    }
  }

  /** What the row asks the user, before anything is installed: the confirmation (with a Bun found, or not) or the path form. */
  const panel = shallowRef<"none" | "confirm" | "own-bun">("none");
  const candidates = shallowRef<BunCandidate[]>([]);
  const candidate = computed(() => candidates.value.find((item) => item.status === "usable") ?? null);

  /** The user flipped the switch on and no Bun is set up: say what Fleet will do, and look for a Bun already here. */
  async function openConfirm(): Promise<void> {
    panel.value = "confirm";
    candidates.value = [];
    const found = await findBuns();
    if (panel.value === "confirm") candidates.value = found;
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

  function stop(): void {
    stopListening?.();
    stopListening = null;
  }

  return {
    view,
    job,
    isInstalling,
    isLoaded,
    panel,
    candidates,
    candidate,
    load,
    install,
    cancel,
    setBunPath,
    findBuns,
    openConfirm,
    applyEvent,
    listen,
    stop,
  };
});
