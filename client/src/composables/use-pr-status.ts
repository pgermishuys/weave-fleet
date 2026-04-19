import { onMounted, onUnmounted, shallowRef, watch, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { PrReference } from "@/lib/pr-utils";

export interface PrStatusResponse {
  number: number;
  title: string;
  state: "open" | "closed";
  merged: boolean;
  draft: boolean;
  checksStatus: "pending" | "running" | "success" | "failure" | "none";
  headRef: string;
  url: string;
}

export interface UsePrStatusResult {
  statuses: ShallowRef<Map<string, PrStatusResponse>>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | undefined>;
}

const PR_POLL_INTERVAL_MS = 15_000;

function isTerminalState(status: PrStatusResponse): boolean {
  return status.merged || status.state === "closed";
}

function mapsEqual(left: Map<string, PrStatusResponse>, right: Map<string, PrStatusResponse>): boolean {
  if (left.size !== right.size) {
    return false;
  }

  for (const [url, leftStatus] of left) {
    const rightStatus = right.get(url);
    if (!rightStatus) {
      return false;
    }

    if (
      leftStatus.state !== rightStatus.state
      || leftStatus.merged !== rightStatus.merged
      || leftStatus.checksStatus !== rightStatus.checksStatus
      || leftStatus.draft !== rightStatus.draft
      || leftStatus.title !== rightStatus.title
    ) {
      return false;
    }
  }

  return true;
}

export function usePrStatus(prs: Readonly<ShallowRef<PrReference[]>> | PrReference[]): UsePrStatusResult {
  const statuses = shallowRef(new Map<string, PrStatusResponse>());
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  let timer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;

  function getPrs(): PrReference[] {
    return Array.isArray(prs) ? prs : prs.value;
  }

  function stopPolling(): void {
    if (!timer) {
      return;
    }

    clearInterval(timer);
    timer = undefined;
  }

  async function fetchStatuses(): Promise<void> {
    const currentPrs = getPrs();
    if (currentPrs.length === 0) {
      statuses.value = new Map();
      isLoading.value = false;
      return;
    }

    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }

    const prsToFetch = currentPrs.filter((pr) => {
      const existing = statuses.value.get(pr.url);
      return !existing || !isTerminalState(existing);
    });

    if (prsToFetch.length === 0) {
      isLoading.value = false;
      return;
    }

    const currentRequestId = ++requestId;
    isLoading.value = true;

    try {
      const results = await Promise.allSettled(
        prsToFetch.map(async (pr) => {
          const response = await apiFetch(`/api/integrations/github/repos/${pr.owner}/${pr.repo}/pulls/${pr.number}/status`);
          if (!response.ok) {
            if (response.status === 401) {
              return null;
            }

            throw new Error(`HTTP ${response.status}`);
          }

          return (await response.json()) as PrStatusResponse;
        }),
      );

      if (currentRequestId !== requestId) {
        return;
      }

      const nextStatuses = new Map(statuses.value);
      let hasChanges = false;

      for (const [index, result] of results.entries()) {
        if (result.status === "fulfilled" && result.value !== null) {
          nextStatuses.set(prsToFetch[index].url, result.value);
          hasChanges = true;
        }
      }

      if (hasChanges && !mapsEqual(statuses.value, nextStatuses)) {
        statuses.value = nextStatuses;
      }

      error.value = undefined;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
      }
    }
  }

  function startPolling(): void {
    stopPolling();
    timer = setInterval(() => {
      void fetchStatuses();
    }, PR_POLL_INTERVAL_MS);
  }

  function handleVisibilityChange(): void {
    if (document.visibilityState === "visible") {
      void fetchStatuses();
    }
  }

  onMounted(() => {
    startPolling();
    document.addEventListener("visibilitychange", handleVisibilityChange);
  });

  onUnmounted(() => {
    stopPolling();
    if (typeof document !== "undefined") {
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    }
  });

  watch(
    () => getPrs().map((pr) => pr.url),
    async () => {
      await fetchStatuses();
      startPolling();
    },
    { immediate: true },
  );

  return {
    statuses,
    isLoading,
    error,
  };
}
