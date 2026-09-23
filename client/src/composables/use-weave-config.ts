import { computed, onBeforeUnmount, readonly, shallowRef } from "vue";
import {
  api,
  type WeaveConfigView,
  type WeaveFlavor,
  type WeaveHarnessCheck,
  type WeaveOwnConfig,
  type WeaveSaveResult,
} from "@/api/client";

/** How often Settings re-reads the config while a save is still waiting for busy folders. */
export const APPLY_POLL_MS = 3000;

function errorMessage(error: unknown, response: Response): string {
  if (error && typeof error === "object") {
    const record = error as Record<string, unknown>;
    for (const key of ["error", "message", "detail"]) {
      if (typeof record[key] === "string" && record[key]) return record[key] as string;
    }
  }
  return `HTTP ${response.status}`;
}

function applyPending(view: WeaveConfigView | null): boolean {
  return view?.apply?.folders.some((folder) => !folder.reloaded) ?? false;
}

/**
 * Settings → Weave: which Weave each harness loads, the config Fleet keeps for it, and how far the last save has got.
 * While a save waits for busy folders, it re-reads until they've all reloaded.
 */
export function useWeaveConfig() {
  const view = shallowRef<WeaveConfigView | null>(null);
  const loading = shallowRef(false);
  const error = shallowRef<string | null>(null);
  let poll: ReturnType<typeof setTimeout> | undefined;

  function schedulePoll(): void {
    if (poll !== undefined) clearTimeout(poll);
    poll = undefined;
    if (!applyPending(view.value)) return;
    poll = setTimeout(() => {
      poll = undefined;
      void load();
    }, APPLY_POLL_MS);
  }

  /** `redetect` asks the harnesses again, which can start one; otherwise Fleet answers from what they said last. */
  async function load(redetect = false): Promise<void> {
    loading.value = true;
    try {
      const { data, error: apiError, response } = await api.GET("/api/weave", {
        params: { query: { redetect: redetect || undefined } },
      });
      if (!response.ok) throw new Error(errorMessage(apiError, response));
      view.value = data as unknown as WeaveConfigView;
      error.value = null;
    } catch (caught) {
      error.value = caught instanceof Error ? caught.message : "Couldn't load the Weave config.";
    } finally {
      loading.value = false;
      schedulePoll();
    }
  }

  async function check(flavor: WeaveFlavor, files: Record<string, string>): Promise<WeaveHarnessCheck[]> {
    const { data, error: apiError, response } = await api.POST("/api/weave/check", { body: { flavor, files } });
    if (!response.ok) throw new Error(errorMessage(apiError, response));
    return data as unknown as WeaveHarnessCheck[];
  }

  /** Saves; with source "fleet" the harnesses try it first, and nothing is saved when one of them fails. */
  async function save(source: "own" | "fleet", files: Record<string, string>): Promise<WeaveSaveResult> {
    const { data, error: apiError, response } = await api.PUT("/api/weave", { body: { source, files } });
    if (!response.ok) throw new Error(errorMessage(apiError, response));
    const result = data as unknown as WeaveSaveResult;
    if (result.saved && result.config) {
      view.value = result.config;
      schedulePoll();
    }
    return result;
  }

  async function readOwn(flavor: WeaveFlavor): Promise<WeaveOwnConfig> {
    const { data, error: apiError, response } = await api.GET("/api/weave/own", { params: { query: { flavor } } });
    if (!response.ok) throw new Error(errorMessage(apiError, response));
    return data as unknown as WeaveOwnConfig;
  }

  onBeforeUnmount(() => {
    if (poll !== undefined) clearTimeout(poll);
  });

  return {
    view: computed(() => view.value),
    loading: readonly(loading),
    error: readonly(error),
    load,
    check,
    save,
    readOwn,
  };
}
