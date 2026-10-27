import { computed } from "vue";
import { MISSION_CONTROL_PREFERENCE_KEY } from "@/lib/mission-control";
import { usePreferencesStore } from "@/stores/preferences";

/** Whether Mission control is on: the switch in Settings → Features. Off, there's no rail item, no screen and no request. */
export function useMissionControlFeature() {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();

  const isMissionControlEnabled = computed(() => preferencesStore.get(MISSION_CONTROL_PREFERENCE_KEY, "false") === "true");

  async function setMissionControlEnabled(enabled: boolean): Promise<void> {
    await preferencesStore.set(MISSION_CONTROL_PREFERENCE_KEY, enabled ? "true" : "false");
  }

  return { isMissionControlEnabled, setMissionControlEnabled };
}
