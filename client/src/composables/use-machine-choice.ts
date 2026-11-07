import { computed, shallowRef } from "vue";
import type { MachineTarget } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";

/**
 * The machine a view with its own machine switch shows (Settings, Automations). With "Switch machines without
 * reloading" on it's the machine you're on (the open session's) until you pick another in the view's switch;
 * otherwise, and for a machine that's gone, the live one. `reset` forgets a pick, so the view opens on the machine
 * you're on. Each view makes its own choice, so a pick in one doesn't move the other.
 */
export function createMachineChoice() {
  /** The machine picked in the view's switch; null until picked since the view opened. */
  const picked = shallowRef<string | null>(null);

  function useChoice() {
    const machines = useMachinesStore();

    const enabled = computed(() => machines.opensInPlace && machines.hasMachines);
    const key = computed(() => {
      if (!enabled.value) return machines.liveKey;
      const wanted = picked.value;
      return wanted && machines.entries.some((entry) => entry.key === wanted) ? wanted : machines.currentKey;
    });
    const entry = computed(() => machines.entries.find((candidate) => candidate.key === key.value) ?? machines.live);
    const target = computed<MachineTarget>(() => machines.targetOfKey(key.value));
    /** Another machine than the live one. */
    const isOther = computed(() => key.value !== machines.liveKey);

    return { enabled, key, entry, target, isOther, pick: (next: string) => { picked.value = next; } };
  }

  return { useChoice, reset: () => { picked.value = null; } };
}

export type MachineChoice = ReturnType<ReturnType<typeof createMachineChoice>["useChoice"]>;
