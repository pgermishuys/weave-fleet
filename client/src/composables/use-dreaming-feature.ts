import { computed } from "vue";
import { MEMORY_PREFERENCE_KEY } from "@/lib/agent-memory";
import { useMachineTarget } from "@/lib/machine-target";
import { usePreferencesStore } from "@/stores/preferences";

/** Whether Dreaming shows: whenever Memory is on. It has no switch of its own; Memory off, nothing shows and no request is made. */
export function useDreamingFeature() {
  const preferencesStore = usePreferencesStore(useMachineTarget());
  preferencesStore.ensureLoaded();

  const isDreamingEnabled = computed(() => preferencesStore.get(MEMORY_PREFERENCE_KEY, "false") === "true");

  return { isDreamingEnabled };
}
