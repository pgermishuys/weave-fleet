import { computed, shallowRef } from "vue";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * An on/off switch kept in a user preference: whether it is on, whether a save is under way, and the toggle.
 * `defaultOn` is what the preference means until the user chooses: on unless it is "false", or off unless it is "true".
 */
export function usePreferenceSwitch(key: string, options: { defaultOn?: boolean; afterSave?: () => void } = {}) {
  const preferencesStore = usePreferencesStore();
  const enabled = computed(() => {
    const value = preferencesStore.get(key, options.defaultOn ? "true" : "false");
    return options.defaultOn ? value !== "false" : value === "true";
  });
  const saving = shallowRef(false);

  async function toggle(): Promise<void> {
    saving.value = true;
    try {
      await preferencesStore.set(key, enabled.value ? "false" : "true");
      options.afterSave?.();
    } finally {
      saving.value = false;
    }
  }

  return { enabled, saving, toggle };
}
