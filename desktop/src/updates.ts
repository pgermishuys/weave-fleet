import { EventEmitter } from "node:events";
import type { AppUpdater, ProgressInfo, UpdateInfo } from "electron-updater";

/**
 * How the app updates itself while it's unsigned. Windows and the AppImage download in the background and install
 * when you restart or quit. macOS can't install into an unsigned app, and the .deb would need a password prompt, so
 * those only say a new version is out and link to it.
 */
export type UpdateMode = "install" | "notify" | "off";

export function updateMode(input: { packaged: boolean; platform: NodeJS.Platform; env: NodeJS.ProcessEnv }): UpdateMode {
  const { packaged, platform, env } = input;
  if (env.FLEET_DESKTOP_UPDATES === "off" || !packaged) return "off";
  if (platform === "win32") return "install";
  if (platform === "linux") return env.APPIMAGE ? "install" : "notify";
  return "notify";
}

export type UpdateStatus = "off" | "idle" | "checking" | "available" | "downloading" | "ready" | "error";

/** What the UI shows (window.fleetDesktop.getUpdateState). */
export interface UpdateState {
  status: UpdateStatus;
  mode: UpdateMode;
  currentVersion: string;
  /** The newer version, once there is one. */
  version?: string;
  /** Download progress, 0–100. */
  percent?: number;
  /** Where to download it by hand (notify mode). */
  releaseUrl?: string;
  error?: string;
  checkedAt?: string;
}

export type UpdaterEvent =
  | { type: "checking" }
  | { type: "available"; version: string }
  | { type: "not-available" }
  | { type: "progress"; percent: number }
  | { type: "downloaded"; version: string }
  | { type: "error"; message: string };

export const RELEASES = "https://github.com/pgermishuys/fleet-releases/releases";

/** The next state after an updater event. Pure, so the rules can be tested without electron-updater. */
export function nextState(state: UpdateState, event: UpdaterEvent, now: Date): UpdateState {
  const base = { mode: state.mode, currentVersion: state.currentVersion };
  switch (event.type) {
    case "checking":
      // A background re-check doesn't hide an update that's already waiting.
      return state.status === "ready" || state.status === "downloading" ? state : { ...base, status: "checking" };
    case "available":
      return state.mode === "install"
        ? { ...base, status: "downloading", version: event.version, percent: 0, checkedAt: now.toISOString() }
        : { ...base, status: "available", version: event.version, releaseUrl: `${RELEASES}/tag/v${event.version}`, checkedAt: now.toISOString() };
    case "not-available":
      return state.status === "ready" ? state : { ...base, status: "idle", checkedAt: now.toISOString() };
    case "progress":
      return state.status === "downloading" ? { ...state, percent: Math.round(event.percent) } : state;
    case "downloaded":
      return { ...base, status: "ready", version: event.version, checkedAt: state.checkedAt ?? now.toISOString() };
    case "error":
      // A failed background check keeps a downloaded update installable.
      return state.status === "ready" ? state : { ...base, status: "error", error: event.message, checkedAt: now.toISOString() };
  }
}

const CHECK_EVERY_MS = 4 * 60 * 60 * 1000;
const FIRST_CHECK_AFTER_MS = 10_000;

/** Wraps electron-updater: checks now and every four hours, and keeps an UpdateState for the UI. */
export class Updates extends EventEmitter<{ state: [UpdateState] }> {
  private current: UpdateState;
  private timer: NodeJS.Timeout | undefined;

  constructor(
    private readonly updater: AppUpdater | null,
    mode: UpdateMode,
    currentVersion: string,
  ) {
    super();
    this.current = { status: mode === "off" ? "off" : "idle", mode, currentVersion };
    if (!updater || mode === "off") return;

    updater.autoDownload = mode === "install";
    updater.autoInstallOnAppQuit = mode === "install";
    updater.allowPrerelease = false;
    updater.on("checking-for-update", () => this.apply({ type: "checking" }));
    updater.on("update-available", (info: UpdateInfo) => this.apply({ type: "available", version: info.version }));
    updater.on("update-not-available", () => this.apply({ type: "not-available" }));
    updater.on("download-progress", (progress: ProgressInfo) => this.apply({ type: "progress", percent: progress.percent }));
    updater.on("update-downloaded", (info: UpdateInfo) => this.apply({ type: "downloaded", version: info.version }));
    updater.on("error", (error: Error) => this.apply({ type: "error", message: error.message }));
  }

  get state(): UpdateState {
    return this.current;
  }

  start(): void {
    if (this.current.mode === "off") return;
    this.timer = setTimeout(() => {
      void this.check();
      this.timer = setInterval(() => void this.check(), CHECK_EVERY_MS);
    }, FIRST_CHECK_AFTER_MS);
  }

  stop(): void {
    clearTimeout(this.timer);
    clearInterval(this.timer);
  }

  async check(): Promise<UpdateState> {
    if (!this.updater || this.current.mode === "off") return this.current;
    try {
      await this.updater.checkForUpdates();
    } catch (error) {
      this.apply({ type: "error", message: (error as Error).message });
    }
    return this.current;
  }

  /** Quits and installs a downloaded update, then starts the new version. The caller stops Fleet first. */
  install(): void {
    if (this.current.status === "ready") this.updater?.quitAndInstall(true, true);
  }

  private apply(event: UpdaterEvent): void {
    this.current = nextState(this.current, event, new Date());
    this.emit("state", this.current);
  }
}
