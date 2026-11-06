import { watch } from "vue";
import { usePreferencesStore } from "@/stores/preferences";
import {
  DISABLED_PLUGINS_PREFERENCE_KEY,
  disabledPluginIds,
  hasCachedDisabledPlugins,
  pluginManifests,
  parseDisabledPlugins,
  setDisabledPlugins,
  setPluginEnabled,
} from "./registry";

/**
 * Keeps which plugins are off in a server preference, like Fleet's other feature switches, so every browser and the
 * phone app agree. Call once, near the root: it loads preferences and applies the list when it arrives.
 * A list this browser kept before the preference existed (the old localStorage copy) moves up once.
 */
export function usePluginPreferenceSync(): void {
  const preferences = usePreferencesStore();
  preferences.ensureLoaded();

  watch(
    () => preferences.hasFetched,
    (fetched) => {
      if (!fetched) return;
      const raw = preferences.preferences[DISABLED_PLUGINS_PREFERENCE_KEY];
      if (raw === undefined) {
        // Nothing saved on the server yet: carry over what this browser had, once.
        const carried = disabledPluginIds();
        if (carried.size > 0 && hasCachedDisabledPlugins()) {
          void preferences.set(DISABLED_PLUGINS_PREFERENCE_KEY, JSON.stringify([...carried]));
        }
        setDisabledPlugins(carried);
        return;
      }
      setDisabledPlugins(parseDisabledPlugins(raw));
    },
    { immediate: true },
  );
}

/** Turns a plugin on or off for everyone using this server; the page changes at once, the save follows. */
export async function setPluginEnabledEverywhere(pluginId: string, enabled: boolean): Promise<void> {
  const own = pluginManifests.get(pluginId)?.ownSwitch;
  if (own) {
    await own.set(enabled);
    return;
  }
  setPluginEnabled(pluginId, enabled);
  await usePreferencesStore().set(DISABLED_PLUGINS_PREFERENCE_KEY, JSON.stringify([...disabledPluginIds()]));
}
