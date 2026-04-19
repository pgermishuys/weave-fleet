import { onMounted, onUnmounted, readonly, ref, type Ref } from "vue";

const STORAGE_KEY = "command-palette-history";
const MAX_RECENT = 10;

const recentIdsState = ref<string[]>([]);
let initialized = false;

const listeners = new Set<() => void>();

function readFromStorage(): string[] {
  if (typeof window === "undefined") {
    return [];
  }

  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as string[]) : [];
  } catch {
    return [];
  }
}

function syncFromStorage(): void {
  recentIdsState.value = readFromStorage();
}

function emitChange(): void {
  syncFromStorage();
  for (const listener of listeners) {
    listener();
  }
}

function ensureInitialized(): void {
  if (initialized) {
    return;
  }

  syncFromStorage();
  initialized = true;
}

export interface UseCommandHistoryResult {
  recentIds: Readonly<Ref<readonly string[]>>;
  recordUsage: (commandId: string) => void;
  clearHistory: () => void;
}

export function useCommandHistory(): UseCommandHistoryResult {
  function handleStorage(event: StorageEvent): void {
    if (event.storageArea !== localStorage || event.key !== STORAGE_KEY) {
      return;
    }

    emitChange();
  }

  onMounted(() => {
    ensureInitialized();
    listeners.add(syncFromStorage);
    window.addEventListener("storage", handleStorage);
  });

  onUnmounted(() => {
    listeners.delete(syncFromStorage);
    window.removeEventListener("storage", handleStorage);
  });

  function recordUsage(commandId: string): void {
    try {
      const current = readFromStorage();
      const updated = [commandId, ...current.filter((id) => id !== commandId)].slice(0, MAX_RECENT);
      localStorage.setItem(STORAGE_KEY, JSON.stringify(updated));
      emitChange();
    } catch {
      // localStorage unavailable
    }
  }

  function clearHistory(): void {
    try {
      localStorage.removeItem(STORAGE_KEY);
      emitChange();
    } catch {
      // localStorage unavailable
    }
  }

  return {
    recentIds: readonly(recentIdsState),
    recordUsage,
    clearHistory,
  };
}
