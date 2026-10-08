import { onMounted, onUnmounted } from "vue";
import { onReconnect, setPresence } from "@/composables/use-weave-socket";
import { liveTarget } from "@/lib/machine-target";

/** How often a window says it's still there; the server forgets one after 90 s. */
export const PRESENCE_INTERVAL_MS = 30_000;

/** A phone: installed to the Home Screen, or a narrow screen. Everything else is a computer at a desk. */
export function currentFormFactor(): "desktop" | "phone" {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return "desktop";
  const standalone = window.matchMedia("(display-mode: standalone)").matches;
  const narrow = window.matchMedia("(max-width: 716px)").matches;
  return standalone || narrow ? "phone" : "desktop";
}

/**
 * Reports whether this window is on screen, every 30 seconds and whenever that changes, so a phone set to "quiet
 * while I'm at the desk" stays quiet while Fleet is open on a computer. `formFactor` overrides the guess.
 */
export function useDeskPresence(formFactor?: "desktop" | "phone"): void {
  let timer: ReturnType<typeof setInterval> | null = null;
  let stopReconnect: (() => void) | null = null;

  const report = () => setPresence(liveTarget(), document.visibilityState === "visible", formFactor ?? currentFormFactor());

  onMounted(() => {
    report();
    timer = setInterval(report, PRESENCE_INTERVAL_MS);
    document.addEventListener("visibilitychange", report);
    stopReconnect = onReconnect(liveTarget(), report);
  });

  onUnmounted(() => {
    if (timer) clearInterval(timer);
    document.removeEventListener("visibilitychange", report);
    stopReconnect?.();
  });
}
