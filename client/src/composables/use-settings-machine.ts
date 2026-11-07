import { computed, shallowRef } from "vue";
import type { MachineTarget } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";

/** The machine picked in the settings menu; null until picked since Settings opened. */
const picked = shallowRef<string | null>(null);

/**
 * The machine Settings shows. With "Switch machines without reloading" on it's the machine you're on (the open
 * session's) until you pick another in the settings menu; otherwise, and for a machine that's gone, the live one.
 */
export function useSettingsMachine() {
  const machines = useMachinesStore();

  const enabled = computed(() => machines.opensInPlace && machines.hasMachines);
  const key = computed(() => {
    if (!enabled.value) return machines.liveKey;
    const wanted = picked.value;
    return wanted && machines.entries.some((entry) => entry.key === wanted) ? wanted : machines.currentKey;
  });
  const entry = computed(() => machines.entries.find((candidate) => candidate.key === key.value) ?? machines.live);
  const target = computed<MachineTarget>(() => machines.targetOfKey(key.value));
  /** Another machine than the live one: pages that don't ask it yet say so instead of showing the live one's. */
  const isOther = computed(() => key.value !== machines.liveKey);

  return { enabled, key, entry, target, isOther, pick: (next: string) => { picked.value = next; } };
}

/** Settings opens on the machine you're on: forget a pick made on an earlier visit. */
export function resetSettingsMachine(): void {
  picked.value = null;
}
