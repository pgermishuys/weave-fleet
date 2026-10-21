import { onMounted, onUnmounted, shallowRef, watch } from "vue";
import { describeUpdatedNotice } from "@/lib/fleet-updates";
import { useFleetUpdatesWatch } from "@/composables/use-fleet-updates";
import { HOME_MACHINE_KEY } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { useNoticesStore } from "@/stores/notices";

const RECHECK_MS = 60_000;

/** The version of the home Fleet this window first saw, which is the one its JavaScript came with. */
const loadedAgainst = shallowRef<string | null>(null);

/** Whether the home Fleet runs another version than this window loaded against. */
export function isWindowStale(version: string | null | undefined): boolean {
  return Boolean(version && loadedAgainst.value && version !== loadedAgainst.value);
}

/** The action that reloads this window onto the Fleet's new version. */
export function reloadAction(): { label: string; tone: "primary"; run: () => void } {
  return { label: "Reload", tone: "primary", run: () => window.location.reload() };
}

/**
 * A window keeps the JavaScript it loaded when its Fleet updates itself, and only the window that pressed Restart
 * reloads. So this reads the Fleet the window loaded from now and then (a minute apart while the window is showing,
 * and when it comes back to the front): when it reports another version than the first this window saw, it offers
 * a reload. With the Updates switch on, Fleet's own "updated to" notice carries that Reload (useFleetUpdateNotices), so
 * this posts none of its own when that one is coming.
 */
export function useStaleWindowNotice(): void {
  const machines = useMachinesStore();
  const notices = useNoticesStore();
  const { enabled: updatesOn } = useFleetUpdatesWatch();
  loadedAgainst.value = null;
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
      if (!loadedAgainst.value) {
        loadedAgainst.value = version;
        return;
      }
      if (version === loadedAgainst.value) return;

      const home = machines.entries.find((entry) => entry.key === HOME_MACHINE_KEY);
      if (updatesOn.value && home && describeUpdatedNotice(home.key, home.name, version, home.info?.update?.lastUpdate, new Date())) return;

      const id = `fleet-window-stale:${version}`;
      if (notices.has(id)) return;
      notices.post({
        id,
        title: `Fleet updated to ${version}`,
        body: "This window is still running the old version. Reload to use the new one.",
        chip: "Reload for the new version",
        onChipClick: () => window.location.reload(),
        actions: [
          reloadAction(),
          { label: "Later", run: () => notices.settle(id) },
        ],
      });
    },
    { immediate: true },
  );
}
