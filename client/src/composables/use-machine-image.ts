import { onScopeDispose, shallowRef, toValue, watch, type MaybeRefOrGetter, type ShallowRef } from "vue";
import { apiUrlOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import { machineRequestInit } from "@/lib/machines";

/**
 * An image Fleet serves at `path`, on the machine the component asks (see `useMachineTarget`), as something an
 * `<img>` can show. On the home machine that's the URL itself: the cookie goes along. Another machine wants its token, which an `<img>` can't send, so the picture is fetched with it and
 * shown from a blob URL. `failed` turns true when it can't be loaded either way.
 */
export function useMachineImage(path: MaybeRefOrGetter<string | null>): { src: ShallowRef<string | null>; failed: ShallowRef<boolean> } {
  const { connection: machine } = useMachineTarget();
  const src = shallowRef<string | null>(null);
  const failed = shallowRef(false);
  let objectUrl: string | null = null;
  let generation = 0;

  function release(): void {
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    objectUrl = null;
  }

  watch(() => toValue(path), async (nextPath) => {
    const current = ++generation;
    release();
    failed.value = false;
    const next = nextPath ? apiUrlOn(machine, nextPath) : null;
    if (!next || !machine) {
      src.value = next;
      return;
    }

    src.value = null;
    try {
      const response = await fetch(next, machineRequestInit(machine));
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const blob = await response.blob();
      if (current !== generation) return;
      objectUrl = URL.createObjectURL(blob);
      src.value = objectUrl;
    } catch {
      if (current === generation) failed.value = true;
    }
  }, { immediate: true });

  onScopeDispose(release);

  return { src, failed };
}
