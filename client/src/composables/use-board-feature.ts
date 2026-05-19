import { computed } from "vue";
import { usePreferencesStore } from "@/stores/preferences";

export const BOARD_FEATURE_ENABLED_KEY = "features.board.enabled";

export function useBoardFeature() {
  const preferencesStore = usePreferencesStore();

  preferencesStore.ensureLoaded();

  const isBoardFeatureEnabled = computed(
    () => preferencesStore.get(BOARD_FEATURE_ENABLED_KEY, "false") === "true",
  );

  async function setBoardFeatureEnabled(enabled: boolean): Promise<void> {
    await preferencesStore.set(BOARD_FEATURE_ENABLED_KEY, enabled ? "true" : "false");
  }

  return {
    isBoardFeatureEnabled,
    setBoardFeatureEnabled,
  };
}
