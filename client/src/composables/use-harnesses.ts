import { readonly, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type { HarnessInfo } from "@/api/client";
import { liveMachineKey, readSaved, writeSaved } from "@/lib/saved-per-machine";

export interface UseHarnessesResult {
  harnesses: Readonly<Ref<readonly HarnessInfo[]>>;
  /** Asking the machine now. With a saved list on screen, that's a check behind it, not a wait. */
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  /** Asks the machine to check every harness again now (Check again), and waits for it. */
  refresh: () => Promise<void>;
}

/** Lists mounted within this long of the last answer use it without asking; a page mounts one per session row. */
const REUSE_FOR_MS = 10_000;
const SAVED_KIND = "harnesses";

interface SavedHarnesses {
  harnesses: HarnessInfo[];
  savedAt: number;
}

/**
 * One machine's harness list, shared by every `useHarnesses` on screen. It starts from the list saved on the last
 * visit, so the composer and Settings draw at once; the machine is asked behind it.
 */
interface HarnessList {
  machineKey: string;
  harnesses: ShallowRef<HarnessInfo[]>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | undefined>;
  /** When this page last had an answer from the machine; 0 until it has one. */
  answeredAt: number;
  /** Numbers each request, so a slow older answer can't overwrite a newer one (checks overlap while an update runs). */
  latestRequest: number;
  /** The plain request running now, which every list mounted meanwhile shares. */
  running: Promise<void> | undefined;
}

const lists = new Map<string, HarnessList>();

function listFor(machineKey: string): HarnessList {
  let list = lists.get(machineKey);
  if (!list) {
    const saved = readSaved<SavedHarnesses>(SAVED_KIND, machineKey);
    list = {
      machineKey,
      harnesses: shallowRef(Array.isArray(saved?.harnesses) ? saved.harnesses : []),
      isLoading: shallowRef(false),
      error: shallowRef(undefined),
      answeredAt: 0,
      latestRequest: 0,
      running: undefined,
    };
    lists.set(machineKey, list);
  }
  return list;
}

/**
 * Asks the machine for its harnesses. Fleet answers from its last check at once; `fresh` makes it check every harness
 * again first (a second or so), for Check again and for anything that just changed a harness.
 */
function load(list: HarnessList, fresh: boolean): Promise<void> {
  if (!fresh && list.running) return list.running;

  const request = ++list.latestRequest;
  list.isLoading.value = true;
  list.error.value = undefined;

  const answer = (async () => {
    try {
      const { data, error, response } = await api.GET("/api/harnesses", fresh ? { params: { query: { fresh: true } } } : {});
      if (request !== list.latestRequest) return;
      if (error || !response.ok) {
        const payload = error as { error?: string } | undefined;
        throw new Error(payload?.error ?? `HTTP ${response.status}`);
      }

      const harnesses = data as unknown as HarnessInfo[];
      list.harnesses.value = harnesses;
      list.answeredAt = Date.now();
      writeSaved(SAVED_KIND, list.machineKey, { harnesses, savedAt: list.answeredAt } satisfies SavedHarnesses);
    } catch (fetchError) {
      if (request === list.latestRequest) {
        list.error.value = fetchError instanceof Error ? fetchError.message : "Failed to fetch harnesses";
      }
    } finally {
      if (request === list.latestRequest) list.isLoading.value = false;
    }
  })();

  if (!fresh) {
    list.running = answer;
    const forget = () => {
      if (list.running === answer) list.running = undefined;
    };
    void answer.then(forget, forget);
  }
  return answer;
}

/**
 * Tells every list on screen to check again, e.g. after installing a harness: the machine checks every harness, and
 * every `useHarnesses` gets the answer.
 */
export function refreshAllHarnesses(): void {
  void load(listFor(liveMachineKey()), true);
}

/** For tests: forget every machine's list, on the page and saved. */
export function forgetHarnessLists(): void {
  lists.clear();
}

export function useHarnesses(): UseHarnessesResult {
  const list = listFor(liveMachineKey());

  if (Date.now() - list.answeredAt >= REUSE_FOR_MS) void load(list, false);

  return {
    harnesses: readonly(list.harnesses),
    isLoading: readonly(list.isLoading),
    error: readonly(list.error),
    refresh: () => load(list, true),
  };
}
