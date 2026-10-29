import { computed, onBeforeUnmount, watch, type Ref } from "vue";
import type { HarnessInfo } from "@/api/client";
import { refreshAllHarnesses } from "@/composables/use-harnesses";

/** How often Fleet checks on a harness update that waits or runs. */
export const HARNESS_UPDATE_POLL_MS = 2000;

/**
 * While a harness update waits or runs, checks on it every 2 seconds, so whatever shows that harness (its tab on
 * Harnesses, its row on Updates & about) follows the update to its end.
 */
export function useHarnessUpdatePoll(harnesses: Readonly<Ref<readonly HarnessInfo[]>>): void {
  let poll: ReturnType<typeof setInterval> | undefined;
  const isUpdating = computed(() =>
    harnesses.value.some((harness) => harness.update?.job?.phase === "waiting" || harness.update?.job?.phase === "running"));

  watch(isUpdating, (updating) => {
    if (updating && poll === undefined) {
      poll = setInterval(refreshAllHarnesses, HARNESS_UPDATE_POLL_MS);
    } else if (!updating && poll !== undefined) {
      clearInterval(poll);
      poll = undefined;
    }
  }, { immediate: true });

  onBeforeUnmount(() => {
    if (poll !== undefined) clearInterval(poll);
  });
}
