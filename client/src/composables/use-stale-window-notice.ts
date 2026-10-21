import { onMounted, onUnmounted, watch } from "vue";
import { HOME_MACHINE_KEY } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { useNoticesStore } from "@/stores/notices";

const RECHECK_MS = 60_000;

/**
 * A window keeps the JavaScript it loaded when its Fleet updates itself, and only the window that pressed Restart
 * reloads. So this reads the Fleet the window loaded from now and then (a minute apart while the window is showing,
 * and when it comes back to the front): when it reports another version than the first this window saw, it offers
 * a reload.
 */
export function useStaleWindowNotice(): void {
  const machines = useMachinesStore();
  const notices = useNoticesStore();
  let loadedAgainst: string | null = null;
  let timer: ReturnType<typeof setInterval> | null = null;

  function recheck(): void {
    if (document.visibilityState === "visible") void machines.loadHome();
  }

  onMounted(() => {
    recheck();
    timer = setInterval(recheck, RECHECK_MS);
    document.addEventListener("visibilitychange", recheck);
  });
  onUnmounted(() => {
    if (timer) clearInterval(timer);
    document.removeEventListener("visibilitychange", recheck);
  });

  watch(
    () => machines.entries.find((entry) => entry.key === HOME_MACHINE_KEY)?.info?.version ?? null,
    (version) => {
      if (!version) return;
      if (!loadedAgainst) {
        loadedAgainst = version;
        return;
      }
      if (version === loadedAgainst) return;

      const id = `fleet-window-stale:${version}`;
      if (notices.has(id)) return;
      notices.post({
        id,
        title: `Fleet updated to ${version}`,
        body: "This window is still running the old version. Reload to use the new one.",
        chip: "Reload for the new version",
        onChipClick: () => window.location.reload(),
        actions: [
          { label: "Reload", tone: "primary", run: () => window.location.reload() },
          { label: "Later", run: () => notices.settle(id) },
        ],
      });
    },
    { immediate: true },
  );
}
