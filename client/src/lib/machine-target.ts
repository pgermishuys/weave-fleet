/**
 * The machine a part of the page asks. Almost everything asks the live machine (`api`). The new-session box can start
 * a session on another machine, so it provides that machine to what it uses (folders, harnesses, agents and models,
 * profiles), and they ask it instead. See `NewSessionComposer.vue`.
 */

import { hasInjectionContext, inject, provide, type InjectionKey } from "vue";
import { api, apiOnMachine, type ApiClient } from "@/api/client";
import { getActiveMachine, type MachineConnection } from "@/lib/machines";
import { machineKeyOf } from "@/lib/saved-per-machine";

export interface MachineTarget {
  /** How the machine's saved answers are filed: its id, or `home`. */
  key: string;
  /** How to reach it; null for home. */
  connection: MachineConnection | null;
  /** It's the machine the app is working in, so everything else on the page asks it too. */
  isLive: boolean;
  api: ApiClient;
}

/** What `provideMachineTarget` provides under; tests provide it through the mount options. */
export const MACHINE_TARGET: InjectionKey<() => MachineTarget> = Symbol("machine-target");

/** The machine the app is working in. */
export function liveTarget(): MachineTarget {
  const connection = getActiveMachine();
  return { key: machineKeyOf(connection), connection, isLive: true, api };
}

/** `connection`'s machine (null: home), which may or may not be the live one. */
export function targetFor(connection: MachineConnection | null): MachineTarget {
  const key = machineKeyOf(connection);
  if (key === machineKeyOf(getActiveMachine())) return liveTarget();
  return { key, connection, isLive: false, api: apiOnMachine(connection) };
}

/**
 * Makes the components below ask the machine `target` returns. It's read when each of them sets up, so a component
 * that should follow a change of machine is rebuilt (keyed by the machine).
 */
export function provideMachineTarget(target: () => MachineTarget): void {
  provide(MACHINE_TARGET, target);
}

/** The machine to ask: the one a component above provided, else the live one (always, outside `setup`). */
export function useMachineTarget(): MachineTarget {
  return (hasInjectionContext() ? inject(MACHINE_TARGET, null)?.() : null) ?? liveTarget();
}
