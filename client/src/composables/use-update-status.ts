import { onMounted, onUnmounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { useMachineTarget, type MachineTarget } from "@/lib/machine-target";

export type UpdateStatusKind =
  | "unknown"
  | "uptodate"
  | "available"
  | "downloading"
  | "staged"
  | "error"
  /** The desktop app updates Fleet; the server doesn't check. */
  | "managed";

export interface UpdateStatus {
  currentVersion: string;
  status: UpdateStatusKind;
  latestVersion: string | null;
  checkedAt: string | null;
  error: string | null;
  downloadBytesReceived: number | null;
  downloadBytesTotal: number | null;
}

export interface UseUpdateStatusResult {
  updateStatus: Readonly<Ref<UpdateStatus | null>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  isUpdateAvailable: Readonly<ShallowRef<boolean>>;
  isUpdateStaged: Readonly<ShallowRef<boolean>>;
  checkForUpdate: () => Promise<void>;
  downloadUpdate: () => Promise<void>;
  refetch: () => Promise<void>;
}

const POLL_INTERVAL_DOWNLOADING_MS = 1_500;
/** The server checks GitHub every few hours; asking it for its state is local and cheap, so the UI hears soon after. */
const REFRESH_INTERVAL_MS = 15 * 60_000;

// ── Shared state, one per machine ──────────────────────────────────────────────
// All consumers of useUpdateStatus() for a machine share the same reactive state and fetch loop. Settings can show
// another machine's update status, so each machine the page asks gets its own.
function createUpdateStatus(target: MachineTarget): UseUpdateStatusResult & { subscribe: () => () => void } {
  const { api } = target;
  const updateStatus = ref<UpdateStatus | null>(null);
  const isLoading = shallowRef(true);
  const isUpdateAvailable = shallowRef(false);
  const isUpdateStaged = shallowRef(false);

  let pollingTimer: ReturnType<typeof setInterval> | undefined;
  let refreshTimer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;
  let subscriberCount = 0;

  async function fetchStatus(): Promise<void> {
    const currentRequestId = ++requestId;
    try {
      const { data, error: apiError } = await api.GET("/api/update/status");
      if (apiError) return;

      if (!data) return;

      const result = data as UpdateStatus;
      if (currentRequestId !== requestId) return;

      updateStatus.value = result;
      isUpdateAvailable.value = result.status === "available" || result.status === "downloading";
      isUpdateStaged.value = result.status === "staged";

      // Poll while downloading.
      if (result.status === "downloading") {
        schedulePolling(POLL_INTERVAL_DOWNLOADING_MS);
      } else {
        stopPolling();
      }
    } catch {
      // Silently ignore — update check failing should not break the UI.
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
      }
    }
  }

  async function checkForUpdate(): Promise<void> {
    await api.POST("/api/update/check");
    await fetchStatus();
  }

  async function downloadUpdate(): Promise<void> {
    await api.POST("/api/update/download");
    // Endpoint returns immediately; start polling for progress.
    schedulePolling(POLL_INTERVAL_DOWNLOADING_MS);
    await fetchStatus();
  }

  function schedulePolling(intervalMs: number): void {
    stopPolling();
    pollingTimer = setInterval(() => {
      void fetchStatus();
    }, intervalMs);
  }

  function stopPolling(): void {
    if (pollingTimer !== undefined) {
      clearInterval(pollingTimer);
      pollingTimer = undefined;
    }
  }

  function onVisibilityChange(): void {
    if (document.visibilityState === "visible") void fetchStatus();
  }

  function startRefreshing(): void {
    refreshTimer = setInterval(() => void fetchStatus(), REFRESH_INTERVAL_MS);
    document.addEventListener("visibilitychange", onVisibilityChange);
  }

  function stopRefreshing(): void {
    clearInterval(refreshTimer);
    refreshTimer = undefined;
    document.removeEventListener("visibilitychange", onVisibilityChange);
  }

  function subscribe(): () => void {
    if (subscriberCount++ === 0) {
      void fetchStatus();
      startRefreshing();
    }
    return () => {
      if (--subscriberCount === 0) {
        stopPolling();
        stopRefreshing();
      }
    };
  }

  return {
    updateStatus: readonly(updateStatus),
    isLoading: readonly(isLoading),
    isUpdateAvailable: readonly(isUpdateAvailable),
    isUpdateStaged: readonly(isUpdateStaged),
    checkForUpdate,
    downloadUpdate,
    refetch: fetchStatus,
    subscribe,
  };
}

// ── Composable ─────────────────────────────────────────────────────────────────

const instances = new Map<string, ReturnType<typeof createUpdateStatus>>();

/** The update status of the machine the page asks (see `useMachineTarget`): the live one unless Settings shows another. */
export function useUpdateStatus(): UseUpdateStatusResult {
  const target = useMachineTarget();
  let instance = instances.get(target.key);
  if (!instance) {
    instance = createUpdateStatus(target);
    instances.set(target.key, instance);
  }
  const { subscribe, ...result } = instance;
  let unsubscribe: (() => void) | undefined;
  onMounted(() => { unsubscribe = subscribe(); });
  onUnmounted(() => unsubscribe?.());
  return result;
}
