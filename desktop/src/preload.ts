import { contextBridge, ipcRenderer, type IpcRendererEvent } from "electron";
import type { UpdateState } from "./updates";

/**
 * `window.fleetDesktop`: what the Fleet UI (and the app's own splash and error pages) can ask the app. The UI
 * checks for it to know it runs inside the app.
 */
const bridge = {
  /** The app's version, which is also the bundled Fleet's. */
  version: ipcRenderer.sendSync("fleet-desktop:version") as string,
  platform: process.platform,
  /** Opens the folder with the app's and Fleet's logs. */
  openLogs: (): Promise<void> => ipcRenderer.invoke("fleet-desktop:open-logs"),
  /** Tries again after Fleet failed to start (the error page's button). */
  retry: (): Promise<void> => ipcRenderer.invoke("fleet-desktop:retry"),
  /** The app's own updates: status, the newer version, download progress. */
  getUpdateState: (): Promise<UpdateState> => ipcRenderer.invoke("fleet-desktop:get-update-state"),
  /** Calls back whenever the update state changes; returns a function that stops it. */
  onUpdateState: (callback: (state: UpdateState) => void): (() => void) => {
    const listener = (_event: IpcRendererEvent, state: UpdateState) => callback(state);
    ipcRenderer.on("fleet-desktop:update-state", listener);
    return () => ipcRenderer.removeListener("fleet-desktop:update-state", listener);
  },
  checkForUpdates: (): Promise<UpdateState> => ipcRenderer.invoke("fleet-desktop:check-for-updates"),
  /**
   * Restarts into a downloaded update, or opens the download page. Asks first if sessions are working, unless
   * `confirmed` says the UI already asked.
   */
  installUpdate: (options?: { confirmed?: boolean }): Promise<void> => ipcRenderer.invoke("fleet-desktop:install-update", options),
  /** Calls back when the UI should open its update card (Help → Check for Updates… found one). */
  onShowUpdate: (callback: () => void): (() => void) => {
    const listener = () => callback();
    ipcRenderer.on("fleet-desktop:show-update", listener);
    return () => ipcRenderer.removeListener("fleet-desktop:show-update", listener);
  },
  /** The window as it is, as a PNG data: URL, for a problem report. */
  captureWindow: (): Promise<string | null> => ipcRenderer.invoke("fleet-desktop:capture-window"),
  /** Calls back when Help → Report a Problem… is chosen in the app's menu. */
  onReportProblem: (callback: () => void): (() => void) => {
    const listener = () => callback();
    ipcRenderer.on("fleet-desktop:report-problem", listener);
    return () => ipcRenderer.removeListener("fleet-desktop:report-problem", listener);
  },
};

contextBridge.exposeInMainWorld("fleetDesktop", bridge);

export type FleetDesktopBridge = typeof bridge;
