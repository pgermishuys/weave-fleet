import { computed } from "vue";
import { liveTarget, useMachineTarget } from "@/lib/machine-target";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * The two kinds of preference on General and Labs. `machine` is the machine Settings shows: its own server reads
 * these (Session recap, Try again when a limit resets, Dreaming…), so they follow the machine picked in Settings.
 * `app` is the live machine's: this app reads them (Show starters, Desktop notifications, the rail's entries…), so
 * they stay this app's whichever machine is picked. On the live machine both are the same store.
 */
export function useSettingsPreferences() {
  const target = useMachineTarget();
  const machine = usePreferencesStore(target);
  const app = usePreferencesStore(liveTarget());
  machine.ensureLoaded();
  app.ensureLoaded();

  /** Another machine whose preferences couldn't be read: its rows show defaults, so they can't be changed. */
  const unreadable = computed(() => !target.isLive && machine.loadFailed);

  return { target, machine, app, unreadable };
}
