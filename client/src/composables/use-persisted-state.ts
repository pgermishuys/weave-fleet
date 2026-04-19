import { onMounted, onUnmounted, shallowRef, type ShallowRef } from "vue";

type PersistedUpdater<T> = T | ((previous: T) => T);
type PersistedSetter<T> = (value: PersistedUpdater<T>) => void;

const keyListeners = new Map<string, Set<() => void>>();

function emitChange(key: string): void {
  const listeners = keyListeners.get(key);
  if (!listeners) {
    return;
  }

  for (const listener of listeners) {
    listener();
  }
}

function readStorageValue<T>(key: string, defaultValue: T): T {
  if (typeof window === "undefined") {
    return defaultValue;
  }

  try {
    const raw = localStorage.getItem(key);
    return raw !== null ? (JSON.parse(raw) as T) : defaultValue;
  } catch {
    return defaultValue;
  }
}

export function usePersistedState<T>(
  key: string,
  defaultValue: T,
): readonly [ShallowRef<T>, PersistedSetter<T>] {
  const state = shallowRef<T>(readStorageValue(key, defaultValue));

  function sync(): void {
    state.value = readStorageValue(key, defaultValue);
  }

  function handleStorage(event: StorageEvent): void {
    if (event.storageArea !== localStorage || event.key !== key) {
      return;
    }

    sync();
  }

  onMounted(() => {
    let listeners = keyListeners.get(key);
    if (!listeners) {
      listeners = new Set();
      keyListeners.set(key, listeners);
    }

    listeners.add(sync);
    window.addEventListener("storage", handleStorage);
    sync();
  });

  onUnmounted(() => {
    window.removeEventListener("storage", handleStorage);

    const listeners = keyListeners.get(key);
    listeners?.delete(sync);
    if (listeners && listeners.size === 0) {
      keyListeners.delete(key);
    }
  });

  function setState(value: PersistedUpdater<T>): void {
    if (typeof window === "undefined") {
      return;
    }

    try {
      const current = readStorageValue(key, defaultValue);
      const next = typeof value === "function"
        ? (value as (previous: T) => T)(current)
        : value;

      localStorage.setItem(key, JSON.stringify(next));
      emitChange(key);
    } catch {
      // localStorage unavailable
    }
  }

  return [state, setState] as const;
}

export function removePersistedKey(key: string): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    localStorage.removeItem(key);
    emitChange(key);
  } catch {
    // localStorage unavailable
  }
}
