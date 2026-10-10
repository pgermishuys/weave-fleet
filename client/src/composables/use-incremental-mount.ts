import { computed, onUnmounted, ref, watch, type ComputedRef, type Ref } from "vue";

// --- Newest first ---
// Mounting a whole conversation at once held the main thread for about a quarter of a second on every switch into a
// long session. The newest messages, the ones on screen, mount first; the older ones follow a batch a frame, above
// the fold. Reaching the top, or asking for a message not mounted yet, mounts the rest at once.
export const FIRST_MOUNTED = 20;
export const MOUNT_BATCH = 20;

export interface IncrementalMount<T> {
  /** The messages to render: all of them, less the older ones still waiting for their batch. */
  mounted: ComputedRef<readonly T[]>;
  /** How many of the oldest messages are still not mounted. */
  unmountedOlder: Ref<number>;
  /** Mounts every message at once, cancelling the batches. */
  mountAll: () => void;
}

/**
 * Mounts a conversation that arrives whole (a session opening) newest first, the older messages a batch a frame.
 * One message at a time mounts as it comes.
 */
export function useIncrementalMount<T>(
  messages: Readonly<Ref<readonly T[]>>,
  sessionId: () => string,
): IncrementalMount<T> {
  const unmountedOlder = ref(0);
  let mountFrame: number | null = null;

  const mounted = computed(() =>
    unmountedOlder.value > 0 ? messages.value.slice(unmountedOlder.value) : messages.value);

  function stopMountingOlder(): void {
    if (mountFrame !== null) {
      cancelAnimationFrame(mountFrame);
      mountFrame = null;
    }
  }

  function mountOlderBatch(): void {
    mountFrame = null;
    unmountedOlder.value = Math.max(0, unmountedOlder.value - MOUNT_BATCH);
    if (unmountedOlder.value > 0) {
      mountFrame = requestAnimationFrame(mountOlderBatch);
    }
  }

  function mountAll(): void {
    stopMountingOlder();
    unmountedOlder.value = 0;
  }

  watch(
    [sessionId, () => messages.value.length],
    ([id, length], [previousId, previousLength]) => {
      // Only a conversation arriving whole: a session opening, from its snapshot or the state kept from the last
      // visit. One message at a time mounts as it comes.
      const arrivesWhole = id !== previousId || previousLength === 0;
      if (!arrivesWhole || length <= FIRST_MOUNTED) {
        if (id !== previousId) {
          mountAll();
        }
        return;
      }

      stopMountingOlder();
      unmountedOlder.value = length - FIRST_MOUNTED;
      // Two frames: the first paints the newest messages, the batches start after it.
      mountFrame = requestAnimationFrame(() => {
        mountFrame = requestAnimationFrame(mountOlderBatch);
      });
    },
  );

  onUnmounted(stopMountingOlder);

  return { mounted, unmountedOlder, mountAll };
}
