import { computed, shallowRef, type ComputedRef, type ShallowRef } from "vue";
import { useHarnesses } from "@/composables/use-harnesses";
import type { HarnessInfo } from "@/api/client";
import { useMachineTarget, type MachineTarget } from "@/lib/machine-target";
import { readSaved, writeSaved } from "@/lib/saved-per-machine";
import { usePreferencesStore } from "@/stores/preferences";

/** The preference that keeps the user's default harness (`HarnessPreferences.DefaultHarnessKey` on the server). */
export const DEFAULT_HARNESS_PREFERENCE_KEY = "defaultHarnessType";

/**
 * The harness a session that names none starts on: the user's pick (`preferred`), else the one the server marks as
 * the default, else the first. Empty while there are no harnesses.
 */
export function resolveDefaultHarness(preferred: string | null | undefined, harnesses: readonly HarnessInfo[]): string {
  return preferred || harnesses.find((harness) => harness.isDefault)?.type || harnesses[0]?.type || "";
}

export interface UseEnabledHarnessesResult {
  /** Every harness Fleet knows, on or off, ready or not. */
  harnesses: ComputedRef<readonly HarnessInfo[]>;
  enabledHarnesses: ComputedRef<HarnessInfo[]>;
  defaultHarnessType: ComputedRef<string>;
  /**
   * Why no harness can start a session: the default harness's reason (e.g. "OpenCode isn't installed: …"),
   * or that every harness is turned off. `null` while the list loads, when it failed to load, or when one is ready.
   */
  noHarnessReason: ComputedRef<string | null>;
}

/**
 * Another machine's default harness, from its own preferences: the new-session box can start a session there. The one
 * saved on the last visit shows at once.
 */
function useDefaultHarnessOn(target: MachineTarget): ShallowRef<string | undefined> {
  const saved = shallowRef(readSaved<string>("default-harness", target.key));
  void (async () => {
    try {
      const { data } = await target.api.GET("/api/preferences");
      const preferences = (data ?? {}) as Record<string, string>;
      saved.value = preferences.defaultHarnessType;
      writeSaved("default-harness", target.key, preferences.defaultHarnessType ?? null);
    } catch {
      // Keep the saved one; the fallback covers a machine that never answered.
    }
  })();
  return saved;
}

/** The harnesses of the machine the page asks (see `useMachineTarget`), and which can start a session. */
export function useEnabledHarnesses(): UseEnabledHarnessesResult {
  const preferencesStore = usePreferencesStore();
  const target = useMachineTarget();
  const { harnesses, isLoading, error } = useHarnesses();
  const otherMachineDefault = target.isLive ? null : useDefaultHarnessOn(target);

  preferencesStore.ensureLoaded();

  const enabledHarnesses = computed<HarnessInfo[]>(() => {
    return harnesses.value.filter((harness) => harness.available && harness.userEnabled);
  });

  const defaultHarnessType = computed<string>(() => resolveDefaultHarness(
    otherMachineDefault ? otherMachineDefault.value : preferencesStore.get(DEFAULT_HARNESS_PREFERENCE_KEY, ""),
    harnesses.value,
  ));

  const noHarnessReason = computed<string | null>(() => {
    // A list that failed to load says nothing about the harnesses; let the session start and report its own error.
    // Only the first load counts as loading: checking again keeps showing the last answer.
    const firstLoad = isLoading.value && harnesses.value.length === 0;
    if (firstLoad || error.value !== undefined || enabledHarnesses.value.length > 0) return null;

    const turnedOn = harnesses.value.filter((harness) => harness.userEnabled);
    const wanted = turnedOn.find((harness) => harness.type === defaultHarnessType.value) ?? turnedOn[0];
    if (!wanted) return "Every harness is turned off.";

    return wanted.reason ?? `${wanted.displayName} isn't ready.`;
  });

  return {
    harnesses: computed(() => harnesses.value),
    enabledHarnesses,
    defaultHarnessType,
    noHarnessReason,
  };
}
