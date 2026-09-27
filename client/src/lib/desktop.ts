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
  /**
   * Restarts into a downloaded update, or opens the download page. The app asks first if sessions are working, unless
   * `confirmed` says the UI already asked. An app from before `confirmed` asks anyway.
   */
  installUpdate(options?: { confirmed?: boolean }): Promise<void>;
  /** Calls back when the app wants the update card open (Help → Check for Updates… found one). Absent in older apps. */
  onShowUpdate?(callback: () => void): () => void;
}

/** The release notes for a version, on the public mirror both the app and the CLI update from. */
export function releaseNotesUrl(version: string): string {
  return `https://github.com/pgermishuys/fleet-releases/releases/tag/v${version.replace(/^v/, "")}`;
}

export function getDesktopBridge(): FleetDesktopBridge | null {
  if (typeof window === "undefined") return null;
  const bridge = (window as { fleetDesktop?: FleetDesktopBridge }).fleetDesktop;
  // An app from before updates existed has the bridge without the update calls.
  return bridge && typeof bridge.getUpdateState === "function" ? bridge : null;
}
