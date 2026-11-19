import { shallowRef } from "vue";
import { loadReach, saveReach, type ReachMode, type ReachState } from "@/lib/reach-api";

/**
 * Who can reach the desktop app, shared by Settings → Machines and its "Add your phone" card so both show the same
 * answer. `null` until read, or when this Fleet can't say.
 */
const state = shallowRef<ReachState | null>(null);

export function useMachineReach() {
  async function load(): Promise<void> {
    try {
      state.value = await loadReach();
    } catch {
      // Without an answer the page behaves as for a Fleet that can't change it.
      state.value = null;
    }
  }

  async function set(mode: ReachMode, ownAddress?: string): Promise<void> {
    state.value = await saveReach(mode, ownAddress);
  }

  return { state, load, set };
}
