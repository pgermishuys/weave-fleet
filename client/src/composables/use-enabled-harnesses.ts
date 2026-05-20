import { computed, type ComputedRef } from "vue";
import { useHarnesses } from "@/composables/use-harnesses";
import type { HarnessInfo } from "@/lib/api-types";
import { usePreferencesStore } from "@/stores/preferences";

const DEFAULT_HARNESS_TYPE = "opencode";

export interface UseEnabledHarnessesResult {
  enabledHarnesses: ComputedRef<HarnessInfo[]>;
  defaultHarnessType: ComputedRef<string>;
}

export function useEnabledHarnesses(): UseEnabledHarnessesResult {
  const preferencesStore = usePreferencesStore();
  const { harnesses } = useHarnesses();

  preferencesStore.ensureLoaded();

  const enabledHarnesses = computed<HarnessInfo[]>(() => {
    return harnesses.value.filter((harness) => harness.available && harness.userEnabled);
  });

  const defaultHarnessType = computed<string>(() => {
    return preferencesStore.get("defaultHarnessType", DEFAULT_HARNESS_TYPE);
  });

  return {
    enabledHarnesses,
    defaultHarnessType,
  };
}
