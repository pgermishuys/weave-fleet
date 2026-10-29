import { computed, onBeforeUnmount, readonly, shallowRef } from "vue";
import {
  api,
  type WeaveConfigView,
  type WeaveFlavor,
  type WeaveHarnessCheck,
  type WeaveOwnConfig,
  type WeavePluginChange,
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

/** What an Add Weave or Remove said, shown on that harness's tab. */
export interface WeavePluginNotice {
  text: string;
  tone: "ok" | "warn" | "error";
}

/**
 * What Fleet said last, so coming back to Settings shows it at once without asking again. Asking can start a harness,
 * so only Check again, a save and a plugin change ask afresh.
 */
let lastView: WeaveConfigView | null = null;
/** The harness whose plugin list Fleet is changing. Every harness's list can be the same opencode.json, so one at a time. */
const changingPlugin = shallowRef<string | null>(null);

/** For tests: forget what Fleet said last. */
export function forgetWeaveConfig(): void {
  lastView = null;
  changingPlugin.value = null;
}

/**
 * Settings → Harnesses → Weave: which Weave each harness loads, the config Fleet keeps for it, and how far the last save has got.
 * While a save waits for busy folders, it re-reads until they've all reloaded.
 */
export function useWeaveConfig() {
  const view = shallowRef<WeaveConfigView | null>(lastView);
  const loading = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const pluginNotices = shallowRef<Readonly<Record<string, WeavePluginNotice>>>({});
  let poll: ReturnType<typeof setTimeout> | undefined;

  function show(next: WeaveConfigView): void {
    view.value = next;
    lastView = next;
  }

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
      show(data as unknown as WeaveConfigView);
      error.value = null;
    } catch (caught) {
      error.value = caught instanceof Error ? caught.message : "Couldn't load the Weave config.";
    } finally {
      loading.value = false;
      schedulePoll();
    }
  }

  /** Asks once: does nothing when Fleet has answered already or is answering now. */
  function ensureLoaded(): void {
    if (view.value === null && !loading.value) void load();
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
      show(result.config);
      schedulePoll();
    }
    return result;
  }

  /**
   * Add Weave (`add`) or take out what it added: Fleet edits the harness's own plugin list and has the harness load
   * the change. The view comes back asked afresh.
   */
  async function changePlugin(harnessType: string, add: boolean): Promise<WeavePluginChange> {
    if (changingPlugin.value !== null) throw new Error("Fleet is still changing another harness's plugins.");
    changingPlugin.value = harnessType;
    try {
      const options = { params: { path: { harnessType } } };
      const { data, error: apiError, response } = add
        ? await api.POST("/api/weave/harnesses/{harnessType}/plugin", options)
        : await api.DELETE("/api/weave/harnesses/{harnessType}/plugin", options);
      if (!response.ok) throw new Error(errorMessage(apiError, response));
      const result = data as unknown as WeavePluginChange;
      show(result.config);
      schedulePoll();
      return result;
    } finally {
      changingPlugin.value = null;
    }
  }

  /** Kept on the page's state, not a tab's, so a tab switch doesn't lose what the change said. */
  function setPluginNotice(harnessType: string, notice: WeavePluginNotice | null): void {
    const next = { ...pluginNotices.value };
    if (notice) next[harnessType] = notice;
    else delete next[harnessType];
    pluginNotices.value = next;
  }

  async function readOwn(flavor: WeaveFlavor): Promise<WeaveOwnConfig> {
    const { data, error: apiError, response } = await api.GET("/api/weave/own", { params: { query: { flavor } } });
    if (!response.ok) throw new Error(errorMessage(apiError, response));
    return data as unknown as WeaveOwnConfig;
  }

  // A save left waiting for busy folders is still followed after coming back.
  schedulePoll();

  onBeforeUnmount(() => {
    if (poll !== undefined) clearTimeout(poll);
  });

  return {
    view: computed(() => view.value),
    loading: readonly(loading),
    error: readonly(error),
    load,
    ensureLoaded,
    check,
    save,
    readOwn,
    changePlugin,
    changingPlugin: readonly(changingPlugin),
    pluginNotices: readonly(pluginNotices),
    setPluginNotice,
  };
}
