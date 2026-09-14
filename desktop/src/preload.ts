import { contextBridge, ipcRenderer } from "electron";

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
};

contextBridge.exposeInMainWorld("fleetDesktop", bridge);

export type FleetDesktopBridge = typeof bridge;
