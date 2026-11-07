/**
 * One Pinia store per machine. Settings can show another machine's data (Folders, Harnesses), so a store those pages
 * read can't be a single instance holding "the" machine's data. `defineMachineStore` defines it once, from a setup
 * that gets the machine it works for, and gives one instance per machine:
 *
 *   export const usePreferencesStore = defineMachineStore("preferences", (target) => { …target.api… });
 *
 * - `usePreferencesStore()` is the live machine's, as it was before (same store id, so nothing else changes).
 * - `usePreferencesStore(useMachineTarget())` is the machine the scope around the caller provides (`MachineScope`),
 *   its own instance with its own state, asking it with `target.api`.
 *
 * The store never reaches for the global `api`; it asks `target`. Pass the target explicitly, so what asks which
 * machine can be read (and linted) from the call site.
 */

import { defineStore } from "pinia";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";

export function defineMachineStore<Id extends string, S extends object>(id: Id, setup: (target: MachineTarget) => S) {
  const definitions = new Map<string, ReturnType<typeof defineStore<Id, S>>>();

  return function useMachineStore(target: MachineTarget = liveTarget()) {
    let use = definitions.get(target.key);
    if (!use) {
      // The live machine keeps the plain id; a machine a scope shows alongside it gets its own.
      use = defineStore((target.isLive ? id : `${id}@${target.key}`) as Id, () => setup(target));
      definitions.set(target.key, use);
    }
    return use();
  };
}
