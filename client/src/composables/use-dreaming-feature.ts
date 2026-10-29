import { computed } from "vue";
import { DREAMING_PREFERENCE_KEY } from "@/lib/dreaming";
import { usePreferencesStore } from "@/stores/preferences";

/** Whether Dreaming is on: the switch in Settings → Labs. Off, Memory shows nothing new and no request is made. */
export function useDreamingFeature() {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();

  const isDreamingEnabled = computed(() => preferencesStore.get(DREAMING_PREFERENCE_KEY, "false") === "true");

  async function setDreamingEnabled(enabled: boolean): Promise<void> {
    await preferencesStore.set(DREAMING_PREFERENCE_KEY, enabled ? "true" : "false");
  }

  return { isDreamingEnabled, setDreamingEnabled };
}
