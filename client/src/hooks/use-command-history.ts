"use client";

import { useCallback, useSyncExternalStore } from "react";

const STORAGE_KEY = "command-palette-history";
const MAX_RECENT = 10;

// Cached snapshot — must return the same reference between calls
// unless the data actually changed. useSyncExternalStore compares
// by reference, so returning a new array each time causes infinite loops.
const EMPTY_SNAPSHOT: string[] = [];
let cachedSnapshot: string[] = EMPTY_SNAPSHOT;

function readFromStorage(): string[] {
  if (typeof window === "undefined") return EMPTY_SNAPSHOT;
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as string[]) : EMPTY_SNAPSHOT;
  } catch {
    return EMPTY_SNAPSHOT;
  }
}

function refreshCache() {
  cachedSnapshot = readFromStorage();
}

function getSnapshot(): string[] {
  return cachedSnapshot;
}

function getServerSnapshot(): string[] {
  return EMPTY_SNAPSHOT;
}

// Simple external store so multiple consumers stay in sync
let listeners: Array<() => void> = [];

function subscribe(listener: () => void) {
  // Hydrate cache on first subscription (client-side)
  if (listeners.length === 0) {
    refreshCache();
  }
  listeners.push(listener);
  return () => {
    listeners = listeners.filter((l) => l !== listener);
  };
}

function emitChange() {
  refreshCache();
  for (const l of listeners) l();
}

export function useCommandHistory() {
  const recentIds = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);

  const recordUsage = useCallback((commandId: string) => {
    try {
      const current = readFromStorage();
      // Remove existing entry and prepend
      const updated = [commandId, ...current.filter((id) => id !== commandId)].slice(0, MAX_RECENT);
      localStorage.setItem(STORAGE_KEY, JSON.stringify(updated));
      emitChange();
    } catch {
      // localStorage unavailable — ignore
    }
  }, []);

  const clearHistory = useCallback(() => {
    try {
      localStorage.removeItem(STORAGE_KEY);
      emitChange();
    } catch {
      // ignore
    }
  }, []);

  return { recentIds, recordUsage, clearHistory };
}
