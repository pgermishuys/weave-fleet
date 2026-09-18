import { computed, type ComputedRef } from "vue";
import { useHarnesses } from "@/composables/use-harnesses";
import type { HarnessInfo } from "@/api/client";
import { usePreferencesStore } from "@/stores/preferences";

const DEFAULT_HARNESS_TYPE = "opencode";

export interface UseEnabledHarnessesResult {
  enabledHarnesses: ComputedRef<HarnessInfo[]>;
  defaultHarnessType: ComputedRef<string>;
  /**
   * Why no harness can start a session: the default harness's reason (e.g. "OpenCode isn't installed: …"),
   * or that every harness is turned off. `null` while the list loads, when it failed to load, or when one is ready.
   */
  noHarnessReason: ComputedRef<string | null>;
}

export function useEnabledHarnesses(): UseEnabledHarnessesResult {
  const preferencesStore = usePreferencesStore();
  const { harnesses, isLoading, error } = useHarnesses();

  preferencesStore.ensureLoaded();

  const enabledHarnesses = computed<HarnessInfo[]>(() => {
    return harnesses.value.filter((harness) => harness.available && harness.userEnabled);
  });

  const defaultHarnessType = computed<string>(() => {
    return preferencesStore.get("defaultHarnessType", DEFAULT_HARNESS_TYPE);
  });

  const noHarnessReason = computed<string | null>(() => {
    // A list that failed to load says nothing about the harnesses; let the session start and report its own error.
    if (isLoading.value || error.value !== undefined || enabledHarnesses.value.length > 0) return null;

    const turnedOn = harnesses.value.filter((harness) => harness.userEnabled);
    const wanted = turnedOn.find((harness) => harness.type === defaultHarnessType.value) ?? turnedOn[0];
    if (!wanted) return "Every harness is turned off.";

    return wanted.reason ?? `${wanted.displayName} isn't ready.`;
  });

  return {
    enabledHarnesses,
    defaultHarnessType,
    noHarnessReason,
  };
}
