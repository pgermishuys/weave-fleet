/**
 * Answers the app keeps between visits, one set per machine, so a list can draw the moment it opens and be checked
 * again behind it. Kept in localStorage; a private window or a full quota only means the next open waits for the
 * machine, as it would have anyway.
 */

import { getActiveMachine, HOME_MACHINE_KEY, type MachineConnection } from "@/lib/machines";

const PREFIX = "weave:saved:";

/** How a machine's saved answers are filed: its id, or `home` for the Fleet that served the page. */
export function machineKeyOf(machine: MachineConnection | null): string {
  return machine?.id ?? HOME_MACHINE_KEY;
}

/** The key of the machine the app is working in. */
export function liveMachineKey(): string {
  return machineKeyOf(getActiveMachine());
}

function storageKey(kind: string, machineKey: string): string {
  return `${PREFIX}${kind}:${machineKey}`;
}

export function readSaved<T>(kind: string, machineKey: string): T | undefined {
  try {
    const raw = window.localStorage.getItem(storageKey(kind, machineKey));
    return raw ? (JSON.parse(raw) as T) : undefined;
  } catch {
    return undefined;
  }
}

export function writeSaved(kind: string, machineKey: string, value: unknown): void {
  try {
    window.localStorage.setItem(storageKey(kind, machineKey), JSON.stringify(value));
  } catch {
    // Private windows and full quotas: the answer lasts until the page closes.
  }
}

export function forgetSaved(kind: string, machineKey: string): void {
  try {
    window.localStorage.removeItem(storageKey(kind, machineKey));
  } catch {
    // Nothing to forget.
  }
}
