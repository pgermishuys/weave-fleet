import { computed, shallowRef } from "vue";
import { useMachineTarget } from "@/lib/machine-target";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * An on/off switch kept in a user preference: whether it is on, whether a save is under way, and the toggle.
 * `defaultOn` is what the preference means until the user chooses: on unless it is "false", or off unless it is "true".
 */
export function usePreferenceSwitch(key: string, options: { defaultOn?: boolean; afterSave?: () => void } = {}) {
  // The preference of the machine the page asks: the live one unless Settings shows another.
  const preferencesStore = usePreferencesStore(useMachineTarget());
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
