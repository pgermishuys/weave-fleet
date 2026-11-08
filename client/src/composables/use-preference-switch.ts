import { computed, shallowRef } from "vue";
import { useMachineTarget, type MachineTarget } from "@/lib/machine-target";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * An on/off switch kept in a user preference: whether it is on, whether a save is under way, and the toggle.
 * `defaultOn` is what the preference means until the user chooses: on unless it is "false", or off unless it is "true".
 * `target` is whose preference it is: by default the machine the page asks (the live one unless Settings shows
 * another); pass `liveTarget()` for a setting of this app, which no other machine reads.
 */
export function usePreferenceSwitch(key: string, options: { defaultOn?: boolean; afterSave?: () => void; target?: MachineTarget } = {}) {
  const preferencesStore = usePreferencesStore(options.target ?? useMachineTarget());
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
