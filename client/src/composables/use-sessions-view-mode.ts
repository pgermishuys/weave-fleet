import { computed, shallowRef } from "vue";

export type SessionsViewMode = "v1" | "v2" | "both";

const STORAGE_KEY = "weave:sessions-view-mode";

function readStoredMode(): SessionsViewMode {
  if (typeof window === "undefined") {
    return "v2";
  }

  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);

    if (stored === "v1" || stored === "v2" || stored === "both") {
      return stored;
    }
  } catch {
    // localStorage unavailable
  }

  return "v2";
}

const viewMode = shallowRef<SessionsViewMode>(readStoredMode());

export function useSessionsViewMode() {
  const isV1Enabled = computed(() => viewMode.value === "v1" || viewMode.value === "both");
  const isV2Enabled = computed(() => viewMode.value === "v2" || viewMode.value === "both");

  function setViewMode(mode: SessionsViewMode): void {
    viewMode.value = mode;

    try {
      window.localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      // localStorage unavailable
    }
  }

  return {
    viewMode,
    isV1Enabled,
    isV2Enabled,
    setViewMode,
  };
}
