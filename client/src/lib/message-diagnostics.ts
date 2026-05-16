/**
 * Persistent diagnostic logger for message lifecycle events.
 *
 * Writes to localStorage under `fleet:message-diagnostics` so entries
 * survive page reloads. Keeps the most recent MAX_ENTRIES to bound storage.
 *
 * Enable by setting `localStorage.setItem('fleet:diag-enabled', '1')` in
 * the browser console. When disabled, all calls are no-ops.
 */

const STORAGE_KEY = "fleet:message-diagnostics";
const ENABLED_KEY = "fleet:diag-enabled";
const MAX_ENTRIES = 500;

export interface DiagEntry {
  /** ISO timestamp */
  ts: string;
  /** Short category tag */
  cat: string;
  /** Human-readable detail */
  msg: string;
  /** Optional structured data */
  data?: Record<string, unknown>;
}

function isEnabled(): boolean {
  try {
    return localStorage.getItem(ENABLED_KEY) === "1";
  } catch {
    return false;
  }
}

function readLog(): DiagEntry[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as DiagEntry[]) : [];
  } catch {
    return [];
  }
}

function writeLog(entries: DiagEntry[]): void {
  try {
    const trimmed = entries.length > MAX_ENTRIES
      ? entries.slice(entries.length - MAX_ENTRIES)
      : entries;
    localStorage.setItem(STORAGE_KEY, JSON.stringify(trimmed));
  } catch {
    // Storage full or unavailable — silently drop
  }
}

/**
 * Append a diagnostic entry. No-op when diagnostics are disabled.
 */
export function diagLog(cat: string, msg: string, data?: Record<string, unknown>): void {
  if (!isEnabled()) return;

  const entry: DiagEntry = {
    ts: new Date().toISOString(),
    cat,
    msg,
    ...(data !== undefined ? { data } : {}),
  };

  // Also log to console for live debugging
   
  console.debug(`[diag:${cat}]`, msg, data ?? "");

  const log = readLog();
  log.push(entry);
  writeLog(log);
}

/**
 * Dump the full diagnostic log to the console and return it.
 * Call from browser console: `fleetDiag.dump()`
 */
export function diagDump(): DiagEntry[] {
  const log = readLog();
   
  console.table(log);
  return log;
}

/**
 * Clear the diagnostic log.
 * Call from browser console: `fleetDiag.clear()`
 */
export function diagClear(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
}

/**
 * Enable or disable diagnostics at runtime.
 * Call from browser console: `fleetDiag.enable()` / `fleetDiag.disable()`
 */
export function diagEnable(): void {
  localStorage.setItem(ENABLED_KEY, "1");
}

export function diagDisable(): void {
  localStorage.removeItem(ENABLED_KEY);
}

// Expose helpers on the window for interactive debugging
if (typeof window !== "undefined") {
  (window as unknown as Record<string, unknown>).fleetDiag = {
    dump: diagDump,
    clear: diagClear,
    enable: diagEnable,
    disable: diagDisable,
    isEnabled,
  };
}
