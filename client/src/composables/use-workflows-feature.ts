import { computed } from "vue";
import { WORKFLOWS_PREFERENCE_KEY } from "@/lib/workflows";
import { usePreferencesStore } from "@/stores/preferences";

/** Whether workflows are on: the switch in Settings → Workflows. Off, there's no rail item and no runner. */
export function useWorkflowsFeature() {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();

  const isWorkflowsEnabled = computed(() => preferencesStore.get(WORKFLOWS_PREFERENCE_KEY, "false") === "true");

  async function setWorkflowsEnabled(enabled: boolean): Promise<void> {
    await preferencesStore.set(WORKFLOWS_PREFERENCE_KEY, enabled ? "true" : "false");
  }

  return { isWorkflowsEnabled, setWorkflowsEnabled };
}
