import { computed } from "vue";
import { JOBS_PREFERENCE_KEY } from "@/lib/jobs";
import { usePreferencesStore } from "@/stores/preferences";

/** Whether Jobs is on: the switch in Settings → Labs. Off, there's no rail item, no screen and no request. */
export function useJobsFeature() {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();

  const isJobsEnabled = computed(() => preferencesStore.get(JOBS_PREFERENCE_KEY, "false") === "true");

  async function setJobsEnabled(enabled: boolean): Promise<void> {
    await preferencesStore.set(JOBS_PREFERENCE_KEY, enabled ? "true" : "false");
  }

  return { isJobsEnabled, setJobsEnabled };
}
