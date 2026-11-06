import { shallowRef } from "vue";
import { MODS_PREFERENCE_KEY } from "@/lib/mods";
import { useModsRuntimeStore } from "@/stores/mods-runtime";
import { useModsStore } from "@/stores/mods";
import { usePreferencesStore } from "@/stores/preferences";
import type { FleetPluginOwnSwitch } from "@/plugins/types";
import ModsSwitchPanel from "./ModsSwitchPanel.vue";

const isSaving = shallowRef(false);

/**
 * The Mods plugin's switch is the same server-side feature preference the Mods switch in Labs always used (`Mods`), so
 * existing users keep their state and nothing else can disagree with it.
 */
export const modsOwnSwitch: FleetPluginOwnSwitch = {
  // The user's choice wins as soon as it is made; until they choose, the server's answer (Fleet's own option).
  isOn: () => useModsStore().isSwitchedOn,
  saving: () => isSaving.value,
  pending: () => useModsRuntimeStore().confirming,
  load: () => {
    void usePreferencesStore().ensureLoaded();
    void useModsStore().loadSwitch();
    void useModsRuntimeStore().load();
  },
  panel: ModsSwitchPanel,
  async set(on: boolean) {
    const runtime = useModsRuntimeStore();
    // The switch stays off while Fleet says what it will install: the user answers there. Flipping it again cancels.
    if (runtime.confirming) {
      runtime.confirming = false;
      return;
    }
    if (on) {
      // Clicked before Fleet said whether Bun is there: ask now, rather than turn Mods on without it.
      if (!runtime.view) await runtime.load();
      const view = runtime.view;
      if (view && !view.bun && !view.configuredPath) {
        runtime.confirming = true;
        return;
      }
    }
    isSaving.value = true;
    try {
      await usePreferencesStore().set(MODS_PREFERENCE_KEY, on ? "true" : "false");
    } finally {
      isSaving.value = false;
    }
  },
};
