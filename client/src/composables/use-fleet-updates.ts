import { computed, onMounted, onUnmounted, shallowRef, watch } from "vue";
import { getDesktopBridge } from "@/lib/desktop";
import {
  describeDesktopUpdate,
  describeMachineUpdate,
  FLEET_UPDATES_PREFERENCE_KEY,
  hasUpdateWaiting,
  osLabel,
  summarizeRows,
  type FleetUpdateRow,
} from "@/lib/fleet-updates";
import { fetchOnMachine } from "@/lib/machines";
import { useDesktopUpdates } from "@/composables/use-desktop-updates";
import { useMachinesStore, type MachineEntry } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/** While a check or a download is under way, the page asks again this often; otherwise the machine store's own poll is enough. */
const BUSY_REFRESH_MS = 3_000;

const DESKTOP_PLATFORMS: Record<string, string> = { darwin: "macos", win32: "windows", linux: "linux" };

/** Settings → Features: whether Settings → Updates exists at all. */
export function useFleetUpdatesEnabled() {
  const preferences = usePreferencesStore();
  preferences.ensureLoaded();
  return computed(() => preferences.get(FLEET_UPDATES_PREFERENCE_KEY, "false") === "true");
}

/**
 * Every Fleet the user runs, with its version and update state: the desktop app when this page runs in it, then home
 * and each machine in Settings → Machines. Machines come from the machine store, so this adds no polling of its own:
 * the store keeps `GET /api/machine` fresh for every machine (a live feed or a poll, as Settings → Features chose).
 */
export function useFleetUpdates() {
  const machines = useMachinesStore();
  const desktop = useDesktopUpdates();
  const bridge = getDesktopBridge();
  const isChecking = shallowRef(false);

  function machineRow(entry: MachineEntry): FleetUpdateRow {
    const info = entry.info ?? null;
    const reachable = entry.isLive ? entry.isHome || machines.liveReachable : !machines.others[entry.key]?.error;
    const state = reachable && !info
      ? { kind: "checking" as const, label: "checking…", tone: "muted" as const, target: null }
      : describeMachineUpdate({
        version: info?.version ?? null,
        update: info?.update ?? null,
        working: entry.capabilities?.sessions.working ?? 0,
        reachable,
      });
    return {
      key: entry.key,
      name: entry.name,
      kind: info?.webApp === false ? "node" : "fleet",
      os: osLabel(entry.os),
      version: info?.version ?? null,
      latest: info?.update?.latestVersion ?? null,
      where: entry.isHome ? "this machine" : entry.baseUrl,
      isLive: entry.isLive,
      state,
    };
  }

  const rows = computed<FleetUpdateRow[]>(() => {
    const list: FleetUpdateRow[] = [];
    if (bridge) {
      list.push({
        key: "desktop",
        name: "This app",
        kind: "app",
        os: osLabel(DESKTOP_PLATFORMS[bridge.platform] ?? bridge.platform),
        version: desktop.state.value?.currentVersion ?? bridge.version,
        latest: desktop.state.value?.version ?? null,
        where: "desktop app",
        isLive: false,
        state: describeDesktopUpdate(desktop.state.value),
      });
    }
    return [...list, ...machines.entries.map(machineRow)];
  });

  const summary = computed(() => summarizeRows(rows.value));
  const isBusy = computed(() => isChecking.value || rows.value.some((row) => row.state.kind === "downloading" || row.state.kind === "checking"));

  /** Asks every reachable Fleet to check for a release now (a node that finds one downloads it), then reads them again. */
  async function checkNow(): Promise<void> {
    isChecking.value = true;
    const refresh = setInterval(() => void machines.refreshUpdates(), BUSY_REFRESH_MS);
    try {
      const checks = machines.entries
        .filter((entry) => entry.info && (entry.isLive ? entry.isHome || machines.liveReachable : !machines.others[entry.key]?.error))
        .map((entry) => fetchOnMachine(entry.connection, "/api/update/check", { method: "POST" }));
      const desktopState = desktop.state.value?.status;
      if (desktopState === "idle" || desktopState === "error") checks.push(desktop.check().then(() => new Response()));
      await Promise.allSettled(checks);
      await machines.refreshUpdates();
    } finally {
      clearInterval(refresh);
      isChecking.value = false;
    }
  }

  return { rows, summary, isBusy, isChecking, checkNow, openDownload: desktop.install, desktopInApp: desktop.inApp };
}

/**
 * Follows the machines while the Updates page or its nav entry is on screen, and says how many Fleets have an update
 * waiting. Only does anything while the switch is on.
 */
export function useFleetUpdatesWatch() {
  const enabled = useFleetUpdatesEnabled();
  const machines = useMachinesStore();
  const { rows } = useFleetUpdates();
  const waiting = computed(() => (enabled.value ? rows.value.filter((row) => hasUpdateWaiting(row.state)).length : 0));
  let stop: (() => void) | null = null;

  function sync(): void {
    if (enabled.value && !stop) stop = machines.startPolling();
    else if (!enabled.value && stop) {
      stop();
      stop = null;
    }
  }

  onMounted(sync);
  watch(enabled, sync);
  onUnmounted(() => stop?.());
  return { enabled, waiting };
}
