import { shallowRef, type ShallowRef } from "vue";
import { apiFetchOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";

export type BrowserPages = "session" | "machine" | "any";

export interface BrowserSettings {
  enabled: boolean;
  pages: BrowserPages;
  scripts: boolean;
}

// One copy per machine: the Browser group saves here, and the "Each harness" table and each harness's tab read it.
const copies = new Map<string, {
  settings: ShallowRef<BrowserSettings>;
  loading: ShallowRef<boolean>;
  saving: ShallowRef<boolean>;
  error: ShallowRef<string | null>;
}>();

/** The agent browser's settings (`/api/agent-browser/settings`), shared by everything in Settings that shows them. */
export function useAgentBrowserSettings() {
  const target = useMachineTarget();
  let copy = copies.get(target.key);
  if (!copy) {
    copy = {
      settings: shallowRef<BrowserSettings>({ enabled: true, pages: "session", scripts: false }),
      loading: shallowRef(true),
      saving: shallowRef(false),
      error: shallowRef<string | null>(null),
    };
    copies.set(target.key, copy);
  }
  const { settings, loading, saving, error } = copy;

  async function load(): Promise<void> {
    loading.value = true;
    try {
      const response = await apiFetchOn(target.connection, "/api/agent-browser/settings");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      settings.value = (await response.json()) as BrowserSettings;
      error.value = null;
    } catch {
      error.value = "Fleet couldn't load the browser settings. Try again in a moment.";
    } finally {
      loading.value = false;
    }
  }

  async function save(change: Partial<BrowserSettings>): Promise<void> {
    const before = settings.value;
    settings.value = { ...before, ...change };
    saving.value = true;
    error.value = null;
    try {
      const response = await apiFetchOn(target.connection, "/api/agent-browser/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(change),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      settings.value = (await response.json()) as BrowserSettings;
    } catch {
      settings.value = before;
      error.value = "Fleet couldn't save that. Try again.";
    } finally {
      saving.value = false;
    }
  }

  return { settings, loading, saving, error, load, save };
}
