import { computed } from "vue";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import { WORKFLOWS_PREFERENCE_KEY } from "@/lib/workflows";
import { usePreferencesStore } from "@/stores/preferences";

/** Whether workflows are on (on `target`'s machine, the live one unless asked for another): the switch in Settings → Labs. Off, there's no rail item and no runner. */
export function useWorkflowsFeature(target: MachineTarget = liveTarget()) {
  const preferencesStore = usePreferencesStore(target);
  preferencesStore.ensureLoaded();

  const isWorkflowsEnabled = computed(() => preferencesStore.get(WORKFLOWS_PREFERENCE_KEY, "false") === "true");

  async function setWorkflowsEnabled(enabled: boolean): Promise<void> {
    await preferencesStore.set(WORKFLOWS_PREFERENCE_KEY, enabled ? "true" : "false");
  }

  return { isWorkflowsEnabled, setWorkflowsEnabled };
}
