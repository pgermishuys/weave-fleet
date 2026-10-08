import { onMounted, onUnmounted, shallowRef } from "vue";
import { onConnectionLost, onDisconnect, onReconnect } from "@/composables/use-weave-socket";
import { useMachineTarget } from "@/lib/machine-target";
import { fetchOnMachine } from "@/lib/machines";

/** Waits between tries while the machine is away, in seconds. */
export const RETRY_STEPS = [2, 4, 8, 15, 30];

/**
 * Whether the session's machine is answering, for the phone session view. The event hub dropping (or the
 * phone going offline) marks it away; it's then asked again with growing waits, shown as "Trying again in 4 s",
 * until it answers or the hub is back. `heard()` records news from it, for "last heard 14:29".
 */
export function useMachineReachability(onBack: () => void) {
  const machine = useMachineTarget();
  const reachable = shallowRef(true);
  const lastHeardAt = shallowRef<number>(Date.now());
  const retryIn = shallowRef(0);
  let attempt = 0;
  let tick: ReturnType<typeof setInterval> | null = null;
  const stops: (() => void)[] = [];

  function heard(): void {
    if (reachable.value) lastHeardAt.value = Date.now();
  }

  function back(): void {
    if (tick) clearInterval(tick);
    tick = null;
    attempt = 0;
    retryIn.value = 0;
    if (reachable.value) return;
    reachable.value = true;
    lastHeardAt.value = Date.now();
    onBack();
  }

  async function probe(): Promise<void> {
    try {
      const response = await fetchOnMachine(machine.connection, "/api/machine");
      if (response.ok) {
        back();
        return;
      }
    } catch {
      // Still away.
    }
    schedule();
  }

  function schedule(): void {
    retryIn.value = RETRY_STEPS[Math.min(attempt, RETRY_STEPS.length - 1)];
    attempt += 1;
    if (tick) clearInterval(tick);
    tick = setInterval(() => {
      retryIn.value = Math.max(0, retryIn.value - 1);
      if (retryIn.value === 0) {
        if (tick) clearInterval(tick);
        tick = null;
        void probe();
      }
    }, 1000);
  }

  function away(): void {
    if (!reachable.value) return;
    reachable.value = false;
    attempt = 0;
    schedule();
  }

  /** Try now (the banner's Retry, or coming back on screen). */
  function retry(): void {
    if (reachable.value) return;
    if (tick) clearInterval(tick);
    tick = null;
    void probe();
  }

  function onVisible(): void {
    if (document.visibilityState === "visible") retry();
  }

  onMounted(() => {
    stops.push(onConnectionLost(machine, away), onDisconnect(machine, away), onReconnect(machine, back));
    window.addEventListener("offline", away);
    window.addEventListener("online", retry);
    document.addEventListener("visibilitychange", onVisible);
    if (typeof navigator !== "undefined" && navigator.onLine === false) away();
  });

  onUnmounted(() => {
    for (const stop of stops) stop();
    window.removeEventListener("offline", away);
    window.removeEventListener("online", retry);
    document.removeEventListener("visibilitychange", onVisible);
    if (tick) clearInterval(tick);
  });

  return { reachable, lastHeardAt, retryIn, heard, retry };
}
