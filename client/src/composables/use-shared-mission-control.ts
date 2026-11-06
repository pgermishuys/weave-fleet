import { effectScope, getCurrentScope, onScopeDispose, type EffectScope } from "vue";
import { useMissionControl } from "@/composables/use-mission-control";

type Mission = ReturnType<typeof useMissionControl>;

let shared: { scope: EffectScope; mission: Mission; users: number } | null = null;

/**
 * Mission control for the fleet-menu and the fleet-detail at once: one set of machine feeds, however many components
 * read it. The feeds open with the first reader and close when the last one goes.
 */
export function useSharedMissionControl(): Mission {
  if (!shared) {
    const scope = effectScope(true);
    shared = { scope, mission: scope.run(() => useMissionControl())!, users: 0 };
  }
  const current = shared;
  current.users += 1;
  if (getCurrentScope()) {
    onScopeDispose(() => {
      current.users -= 1;
      if (current.users > 0) return;
      current.scope.stop();
      if (shared === current) shared = null;
    });
  }
  return current.mission;
}
