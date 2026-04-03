/**
 * Tauri detection and interop utilities.
 *
 * These helpers dynamically import @tauri-apps/api so that the same Next.js
 * bundle works in both the browser and the Tauri webview. When running outside
 * Tauri the functions gracefully return null / no-op.
 */

/** Returns true when running inside a Tauri webview. */
export function isTauri(): boolean {
  return typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
}

/**
 * Listen for a Tauri event. Returns an unlisten function, or null if not in
 * Tauri.
 */
export async function tauriListen<T>(
  event: string,
  handler: (payload: T) => void,
): Promise<(() => void) | null> {
  if (!isTauri()) return null;
  try {
    const { listen } = await import("@tauri-apps/api/event");
    const unlisten = await listen<T>(event, (e) => handler(e.payload));
    return unlisten;
  } catch {
    return null;
  }
}

/**
 * Invoke a Tauri command. Returns the result or throws if not in Tauri.
 */
export async function tauriInvoke<T>(
  command: string,
  args?: Record<string, unknown>,
): Promise<T> {
  if (!isTauri()) throw new Error("Not running in Tauri");
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<T>(command, args);
}

export interface TauriUpdateState {
  channel: "stable" | "dev";
  auto_update: boolean;
  update_available: {
    version: string;
    current_version: string;
  } | null;
  download_in_progress: boolean;
  update_ready_for_restart: boolean;
}

export async function tauriSetUpdatePreferences(
  autoUpdate: boolean,
  channel: "stable" | "dev",
): Promise<void> {
  await tauriInvoke("set_update_preferences", {
    auto_update: autoUpdate,
    channel,
  });
}

export async function tauriGetUpdateState(): Promise<TauriUpdateState> {
  return tauriInvoke<TauriUpdateState>("get_update_state");
}
