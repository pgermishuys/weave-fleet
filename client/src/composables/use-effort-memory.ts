import { onScopeDispose, shallowRef, watch } from "vue";
import { usePreferencesStore } from "@/stores/preferences";
import { useMachineTarget } from "@/lib/machine-target";
import { effortLabel, effortPreferenceKey, rememberedEffort } from "@/lib/effort-memory";
import type { DraftState } from "@/composables/use-draft-state";

interface Options {
  draft: DraftState;
  setEffort: (effort: string) => void;
  harnessType: () => string | undefined;
  /** The model the next prompt runs on (picked, else the session's own). */
  modelKey: () => string;
  modelName: () => string;
  /** The model picked in the composer ("" when none): only a pick says "back on High". */
  picked?: () => string;
  /** The levels that model offers; none means it has no effort. */
  variants: () => readonly string[];
}

/**
 * Effort remembered per harness and model, on the machine the composer targets. The draft takes the remembered
 * level when the model changes and when the draft opens; only `pick` (a composer pick) writes it.
 */
export function useEffortMemory(options: Options) {
  const store = usePreferencesStore(useMachineTarget());
  store.ensureLoaded();
  /** "Opus 5.5: back on High", shown briefly after a model switch brings a level back. */
  const note = shallowRef("");
  let noteTimer: ReturnType<typeof setTimeout> | undefined;
  let appliedFor: string | undefined;
  let lastPicked = options.picked?.() ?? "";

  function key(): string {
    return effortPreferenceKey(options.harnessType(), options.modelKey());
  }

  function apply(): void {
    const level = rememberedEffort(store.preferences[key()], options.variants());
    options.setEffort(level);
  }

  watch(
    () => [store.hasFetched, key(), options.variants().join(",")] as const,
    ([fetched, current, levels]) => {
      if (!fetched || !options.modelKey()) return;
      const pickedChanged = (options.picked?.() ?? "") !== lastPicked;
      lastPicked = options.picked?.() ?? "";
      const first = appliedFor === undefined;
      const changed = appliedFor !== current;
      // A session on a named model knows its key before the catalog brings the model's levels. Until they're here
      // nothing is settled, so the levels arriving count as the draft opening and the remembered level is read then.
      appliedFor = levels ? current : undefined;
      // A draft that already holds a level (opened again within the page) keeps it; a model switch always re-reads.
      if (first && options.draft.effort) return;
      if (!changed) return;
      const before = options.draft.effort;
      apply();
      if (pickedChanged && options.draft.effort && options.draft.effort !== before) {
        note.value = `${options.modelName()}: back on ${effortLabel(options.draft.effort)}`;
        clearTimeout(noteTimer);
        noteTimer = setTimeout(() => { note.value = ""; }, 4000);
      }
    },
    { immediate: true },
  );
  onScopeDispose(() => clearTimeout(noteTimer));

  /** A pick in the composer: the draft holds it and the machine remembers it for this model (Default forgets). */
  function pick(level: string): void {
    options.setEffort(level);
    note.value = "";
    void store.set(key(), level);
  }

  return { pick, note };
}
