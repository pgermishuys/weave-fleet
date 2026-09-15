/**
 * The desktop app's bridge (`window.fleetDesktop`, from desktop/src/preload.ts). Present only when the UI runs in the
 * app's window; a browser tab on the same Fleet has none.
 */

export type DesktopUpdateStatus = "off" | "idle" | "checking" | "available" | "downloading" | "ready" | "error";

/** The app's own updates. Mirrors UpdateState in desktop/src/updates.ts. */
export interface DesktopUpdateState {
  status: DesktopUpdateStatus;
  /** install: downloads and installs on restart. notify: links to the download (unsigned macOS, .deb). */
  mode: "install" | "notify" | "off";
  currentVersion: string;
  version?: string;
  percent?: number;
  releaseUrl?: string;
  error?: string;
  checkedAt?: string;
}

export interface FleetDesktopBridge {
  version: string;
  platform: string;
  openLogs(): Promise<void>;
  retry(): Promise<void>;
  getUpdateState(): Promise<DesktopUpdateState>;
  onUpdateState(callback: (state: DesktopUpdateState) => void): () => void;
  checkForUpdates(): Promise<DesktopUpdateState>;
  installUpdate(): Promise<void>;
}

export function getDesktopBridge(): FleetDesktopBridge | null {
  if (typeof window === "undefined") return null;
  const bridge = (window as { fleetDesktop?: FleetDesktopBridge }).fleetDesktop;
  // An app from before updates existed has the bridge without the update calls.
  return bridge && typeof bridge.getUpdateState === "function" ? bridge : null;
}
