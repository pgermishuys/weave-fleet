import * as path from "node:path";
import { BrowserWindow, Menu, Notification, Tray, app, dialog, ipcMain, nativeImage, nativeTheme, screen, shell } from "electron";
import { findRunningFleet } from "./instance";
import { classifyLink } from "./links";
import { buildAppMenu, showContextMenu } from "./menu";
import { resolvePaths } from "./paths";
import { FleetServer, type FleetServerEvent } from "./server";
import { restorableBounds, SettingsStore } from "./settings";
import { installLoginShellEnv } from "./shell-env";

const STATIC_DIR = path.join(__dirname, "..", "static");
const DEFAULT_PORT = 5000;

const paths = resolvePaths({
  env: process.env,
  platform: process.platform,
  resourcesPath: app.isPackaged ? process.resourcesPath : null,
  appDir: path.join(__dirname, ".."),
});
const serverLog = () => path.join(app.getPath("logs"), "fleet-server.log");

let settings: SettingsStore;
let window: BrowserWindow | null = null;
let tray: Tray | null = null;
let server: FleetServer | null = null;
/** The Fleet the window shows, and whether the app started it (and so stops it on quit). */
let fleet: { url: string; owned: boolean } | null = null;
let quitting = false;

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", showWindow);
  void app.whenReady().then(start);
}

async function start(): Promise<void> {
  settings = new SettingsStore(app.getPath("userData"));
  Menu.setApplicationMenu(
    buildAppMenu(process.platform, {
      openLogs: () => void shell.openPath(app.getPath("logs")),
      openDataFolder: () => void shell.openPath(paths.dataDir),
    }),
  );
  ipcMain.on("fleet-desktop:version", (event) => {
    event.returnValue = app.getVersion();
  });
  ipcMain.handle("fleet-desktop:open-logs", () => shell.openPath(app.getPath("logs")));
  ipcMain.handle("fleet-desktop:retry", () => connect());

  createWindow();
  await installLoginShellEnv(process.env, process.platform);
  await connect();
}

/** Connects to a Fleet that's already running, or starts the bundled one. */
async function connect(): Promise<void> {
  showPage("splash.html", { status: "Starting Fleet…" });
  const running = await findRunningFleet(paths.instanceFile);
  if (running) {
    open({ url: running.url, owned: false });
    return;
  }

  server?.removeAllListeners();
  server = new FleetServer({
    paths,
    env: process.env,
    logFile: serverLog(),
    preferredPort: settings.get().port ?? DEFAULT_PORT,
  });
  server.on("event", (event) => void onServerEvent(event));
  await server.start();
}

async function onServerEvent(event: FleetServerEvent): Promise<void> {
  switch (event.type) {
    case "ready":
      settings.update({ port: event.port });
      if (fleet?.url !== event.url || !window?.webContents.getURL().startsWith(event.url)) {
        open({ url: event.url, owned: true });
      }
      break;
    case "taken": {
      // Someone else started Fleet on this database while we were starting ours: use theirs.
      server = null;
      for (let attempt = 0; attempt < 60; attempt++) {
        const running = await findRunningFleet(paths.instanceFile);
        if (running) {
          open({ url: running.url, owned: false });
          return;
        }
        await new Promise((resolve) => setTimeout(resolve, 500));
      }
      showError("Another Fleet is using your data, but it isn't answering.");
      break;
    }
    case "restarting":
      console.warn(`Fleet stopped (exit code ${event.exitCode}); restarting in ${event.delayMs} ms.`);
      break;
    case "failed":
      showError(event.message);
      break;
  }
}

function open(target: NonNullable<typeof fleet>): void {
  fleet = target;
  void window?.loadURL(target.url);
}

function showPage(page: string, query: Record<string, string>): void {
  void window?.loadFile(path.join(STATIC_DIR, page), { query });
}

function showError(message: string): void {
  fleet = null;
  showPage("error.html", { message, log: serverLog() });
}

function createWindow(): void {
  const saved = settings.get().window;
  const bounds = restorableBounds(saved, screen.getAllDisplays().map((display) => display.workArea));
  window = new BrowserWindow({
    ...(bounds ?? { width: 1360, height: 880 }),
    minWidth: 720,
    minHeight: 480,
    show: false,
    title: "Fleet",
    icon: path.join(STATIC_DIR, "icon.png"),
    autoHideMenuBar: process.platform !== "darwin",
    backgroundColor: nativeTheme.shouldUseDarkColors ? "#0d0d10" : "#F3F2EF",
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      spellcheck: true,
    },
  });
  if (saved?.maximized) window.maximize();
  window.once("ready-to-show", () => window?.show());

  const contents = window.webContents;
  const fleetOrigin = () => (fleet ? new URL(fleet.url).origin : null);
  contents.setWindowOpenHandler(({ url }) => {
    if (classifyLink(url, fleetOrigin()) !== "block") void shell.openExternal(url);
    return { action: "deny" };
  });
  contents.on("will-navigate", (event, url) => {
    if (url.startsWith("file:")) return; // The splash and error pages.
    const target = classifyLink(url, fleetOrigin());
    if (target === "window") return;
    event.preventDefault();
    if (target === "browser") void shell.openExternal(url);
  });
  contents.on("context-menu", (_event, params) => {
    if (window) showContextMenu(window, params);
  });

  let saveTimer: NodeJS.Timeout | undefined;
  const saveBounds = () => {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      if (!window || window.isMinimized() || window.isFullScreen()) return;
      settings.update({ window: { ...window.getNormalBounds(), maximized: window.isMaximized() } });
    }, 400);
  };
  window.on("resize", saveBounds);
  window.on("move", saveBounds);
  window.on("close", onWindowClose);
  window.on("closed", () => {
    window = null;
  });
}

/**
 * Closing the window. On macOS the app stays in the Dock. If the app started Fleet and sessions are working, it
 * keeps running in the tray instead of stopping them. Otherwise closing quits the app, which stops a Fleet it
 * started and leaves one it connected to running.
 */
function onWindowClose(event: Electron.Event): void {
  if (quitting) return;
  if (process.platform === "darwin") {
    event.preventDefault();
    window?.hide();
    return;
  }
  if (!fleet?.owned) return;
  event.preventDefault();
  void workingSessions().then((working) => {
    if (working > 0) hideToTray(working);
    else app.quit();
  });
}

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});

app.on("activate", showWindow);

app.on("before-quit", (event) => {
  if (quitting || !server || !fleet?.owned) {
    quitting = true;
    return;
  }
  event.preventDefault();
  void confirmQuit().then(async (confirmed) => {
    if (!confirmed) return;
    quitting = true;
    await server?.stop();
    app.quit();
  });
});

async function confirmQuit(): Promise<boolean> {
  const working = await workingSessions();
  if (working === 0) return true;
  const { response } = await dialog.showMessageBox({
    type: "warning",
    message: working === 1 ? "A session is still working" : `${working} sessions are still working`,
    detail: "Quitting Fleet stops them.",
    buttons: ["Quit Fleet", "Cancel"],
    defaultId: 1,
    cancelId: 1,
  });
  return response === 0;
}

/** Sessions working in the Fleet the app started, or 0 when that can't be told. */
async function workingSessions(): Promise<number> {
  if (!fleet?.owned) return 0;
  try {
    const response = await fetch(`${fleet.url}/api/desktop/status`, { signal: AbortSignal.timeout(2000) });
    if (!response.ok) return 0;
    const body = (await response.json()) as { workingSessions?: unknown };
    return typeof body.workingSessions === "number" ? body.workingSessions : 0;
  } catch {
    return 0;
  }
}

function hideToTray(working: number): void {
  if (!tray) {
    tray = new Tray(nativeImage.createFromPath(path.join(STATIC_DIR, "tray.png")));
    tray.setContextMenu(
      Menu.buildFromTemplate([
        { label: "Show Fleet", click: showWindow },
        { type: "separator" },
        { label: "Quit Fleet", click: () => app.quit() },
      ]),
    );
    tray.on("click", showWindow);
  }
  tray.setToolTip(working === 1 ? "Fleet: a session is working" : `Fleet: ${working} sessions are working`);
  window?.hide();
  if (Notification.isSupported()) {
    new Notification({
      title: "Fleet is still running",
      body: `${working === 1 ? "A session is" : `${working} sessions are`} still working. Quit from the tray icon when you're done.`,
      silent: true,
    }).show();
  }
}

function showWindow(): void {
  if (!settings) return; // Still starting; the window is on its way.
  if (!window) {
    createWindow();
    if (fleet) void window!.loadURL(fleet.url);
    else void connect();
    return;
  }
  if (window.isMinimized()) window.restore();
  window.show();
  window.focus();
  tray?.destroy();
  tray = null;
}
