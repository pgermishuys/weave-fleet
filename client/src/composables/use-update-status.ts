import { onMounted, onUnmounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

export type UpdateStatusKind = "unknown" | "uptodate" | "available" | "downloading" | "staged" | "error";

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

// ── Module-scoped shared state ─────────────────────────────────────────────────
// All consumers of useUpdateStatus() share the same reactive state and fetch loop.
const updateStatus = ref<UpdateStatus | null>(null);
const isLoading = shallowRef(true);
const isUpdateAvailable = shallowRef(false);
const isUpdateStaged = shallowRef(false);

let pollingTimer: ReturnType<typeof setInterval> | undefined;
let requestId = 0;
let subscriberCount = 0;

async function fetchStatus(): Promise<void> {
  const currentRequestId = ++requestId;
  try {
    const response = await apiFetch("/api/update/status");
    if (!response.ok) return;

    const data = (await response.json()) as UpdateStatus;
    if (currentRequestId !== requestId) return;

    updateStatus.value = data;
    isUpdateAvailable.value = data.status === "available" || data.status === "downloading";
    isUpdateStaged.value = data.status === "staged";

    // Poll while downloading.
    if (data.status === "downloading") {
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
  await apiFetch("/api/update/check", { method: "POST" });
  await fetchStatus();
}

async function downloadUpdate(): Promise<void> {
  await apiFetch("/api/update/download", { method: "POST" });
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

// ── Composable ─────────────────────────────────────────────────────────────────

export function useUpdateStatus(): UseUpdateStatusResult {
  onMounted(() => {
    if (subscriberCount++ === 0) {
      void fetchStatus();
    }
  });

  onUnmounted(() => {
    if (--subscriberCount === 0) {
      stopPolling();
    }
  });

  return {
    updateStatus: readonly(updateStatus),
    isLoading: readonly(isLoading),
    isUpdateAvailable: readonly(isUpdateAvailable),
    isUpdateStaged: readonly(isUpdateStaged),
    checkForUpdate,
    downloadUpdate,
    refetch: fetchStatus,
  };
}
