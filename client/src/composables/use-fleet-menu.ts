import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { useFleetView } from "@/composables/use-fleet-view";
import { useSharedMissionControl } from "@/composables/use-shared-mission-control";
import { fleetViewPath, isSameFleetView, type FleetView } from "@/lib/fleet-views";
import { useMachinesStore } from "@/stores/machines";

export interface FleetMenuItem {
  id: string;
  label: string;
  view: FleetView;
  /** Sessions on the machine that work or wait; for Needs you, the asks waiting. */
  count: number;
  /** A machine that didn't answer last time. */
  unreachable?: boolean;
}

/** The fleet-menu: Needs you first, then one entry per machine from the machines store, and the one open. */
export function useFleetMenu() {
  const router = useRouter();
  const machines = useMachinesStore();
  const mission = useSharedMissionControl();
  const active = useFleetView();

  const needsYou: FleetMenuItem = { id: "needs-you", label: "Needs you", view: { kind: "needs-you" }, count: 0 };
  const attention = computed<FleetMenuItem>(() => ({ ...needsYou, count: mission.waiting.value.length }));

  const machineItems = computed<readonly FleetMenuItem[]>(() => machines.entries.map((entry) => ({
    id: entry.key,
    label: entry.name,
    view: { kind: "machine", machineId: entry.key },
    count: mission.tiles.value.filter((tile) => tile.machineId === entry.key).length,
    unreachable: mission.states.value.find((state) => state.id === entry.key)?.status === "unreachable",
  })));

  function isActive(item: FleetMenuItem): boolean {
    return isSameFleetView(active.value, item.view);
  }

  function select(item: FleetMenuItem): void {
    if (isActive(item)) return;
    void router.navigate({ to: fleetViewPath(item.view) });
  }

  return { attention, machineItems, isActive, select, summary: mission.summary };
}
