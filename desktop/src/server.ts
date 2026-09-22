import { spawn, type ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import * as fs from "node:fs";
import * as net from "node:net";
import * as path from "node:path";
import type { FleetPaths } from "./paths";
import { isReady } from "./instance";

/** Fleet's exit code when another Fleet holds its database lock (FleetInstanceLock.InUseExitCode). */
export const IN_USE_EXIT_CODE = 75;

/** Variables that must not reach Fleet: Electron's own, and listen URLs that would override the port we pick. */
const DROPPED_PREFIXES = ["ELECTRON_"];
const DROPPED_NAMES = ["URLS", "ASPNETCORE_URLS", "DOTNET_URLS", "ASPNETCORE_ENVIRONMENT"];

/**
 * The environment the bundled Fleet runs with: the launcher's settings, under the shared data directory. No login
 * token: Fleet signs in loopback requests on its own, the window's and the app's alike.
 *
 * `Fleet__Host: "127.0.0.1"` is load-bearing for that. Fleet only grants the loopback bypass when it binds to a
 * loopback address, since anything wider can be fronted by a proxy that makes every outside caller look local.
 * Bind the bundled Fleet anywhere else and the window would be asked for a login token it is never given.
 */
export function buildServerEnv(base: NodeJS.ProcessEnv, paths: FleetPaths, port: number): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(base)) {
    if (value === undefined) continue;
    const upper = key.toUpperCase();
    if (DROPPED_NAMES.includes(upper) || DROPPED_PREFIXES.some((prefix) => upper.startsWith(prefix))) continue;
    env[key] = value;
  }
  return {
    ...env,
    ASPNETCORE_ENVIRONMENT: "Production",
    Fleet__Host: "127.0.0.1",
    Fleet__Port: String(port),
    Fleet__DatabasePath: paths.databasePath,
    Fleet__AnalyticsDatabasePath: path.join(paths.dataDir, "fleet-analytics.db"),
    Fleet__DataProtection__KeyPath: path.join(paths.dataDir, "fleet-keys"),
    Fleet__Desktop__Enabled: "true",
  };
}

/**
 * When to restart a Fleet that crashed: straight away the first time, then backing off, and not at all after
 * five crashes inside a minute. Returns the delay in milliseconds, or null to give up.
 */
export function restartDelay(recentCrashes: readonly number[], now: number): number | null {
  const lastMinute = recentCrashes.filter((time) => now - time < 60_000);
  if (lastMinute.length >= 5) return null;
  return lastMinute.length <= 1 ? 0 : 1000 * 2 ** (lastMinute.length - 2);
}

/** The preferred port if it's free, otherwise any free one. A stable port keeps the UI's local storage between runs. */
export async function pickPort(preferred: number | undefined): Promise<number> {
  if (preferred && (await canListen(preferred))) return preferred;
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      server.close(() => (address && typeof address === "object" ? resolve(address.port) : reject(new Error("No port"))));
    });
  });
}

function canListen(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const server = net.createServer();
    server.once("error", () => resolve(false));
    server.listen(port, "127.0.0.1", () => server.close(() => resolve(true)));
  });
}

export interface FleetServerOptions {
  paths: FleetPaths;
  env: NodeJS.ProcessEnv;
  logFile: string;
  preferredPort?: number;
  readyTimeoutMs?: number;
}

export type FleetServerEvent =
  /** Fleet is listening at the URL. Fires again after each restart. */
  | { type: "ready"; url: string; port: number }
  /** Another Fleet took the database lock first: connect to that one instead. */
  | { type: "taken" }
  /** Fleet crashed and is restarting after `delayMs`. */
  | { type: "restarting"; delayMs: number; exitCode: number | null }
  /** Fleet keeps crashing, or never became ready. */
  | { type: "failed"; message: string };

/** Starts the bundled Fleet and keeps it running until `stop()`. */
export class FleetServer extends EventEmitter<{ event: [FleetServerEvent] }> {
  private child: ChildProcess | null = null;
  private stopping = false;
  private readonly crashes: number[] = [];
  private port = 0;

  constructor(private readonly options: FleetServerOptions) {
    super();
  }

  get url(): string {
    return `http://127.0.0.1:${this.port}`;
  }

  get pid(): number | undefined {
    return this.child?.pid;
  }

  async start(): Promise<void> {
    this.stopping = false;
    this.crashes.length = 0;
    this.port = await pickPort(this.port || this.options.preferredPort);
    this.launch();
  }

  /** Closes Fleet's standard input, which stops it cleanly with its agents; kills it if it takes too long. */
  async stop(graceMs = 20_000): Promise<void> {
    this.stopping = true;
    const child = this.child;
    if (!child || child.exitCode !== null || child.signalCode !== null) return;
    const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
    child.stdin?.end();
    if (await settlesWithin(exited, graceMs)) return;
    child.kill("SIGTERM");
    if (await settlesWithin(exited, 5000)) return;
    child.kill("SIGKILL");
    await exited;
  }

  private launch(): void {
    const { paths, env, logFile } = this.options;
    if (!fs.existsSync(paths.serverBinary)) {
      this.emit("event", { type: "failed", message: `Fleet isn't where the app expects it: ${paths.serverBinary}` });
      return;
    }
    fs.mkdirSync(path.join(paths.dataDir, "fleet-keys"), { recursive: true });
    const log = openLog(logFile);
    log.write(`\n── ${new Date().toISOString()} starting ${paths.serverBinary} on ${this.url}\n`);

    const child = spawn(paths.serverBinary, ["--urls", this.url, "--contentRoot", paths.serverContentRoot], {
      cwd: paths.dataDir,
      env: buildServerEnv(env, paths, this.port),
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    this.child = child;
    child.stdout?.pipe(log, { end: false });
    child.stderr?.pipe(log, { end: false });
    child.stdin?.on("error", () => {
      // Fleet went away first; its exit handler deals with it.
    });

    const ready = this.waitUntilReady(child);
    child.once("error", (error) => {
      log.write(`── could not start Fleet: ${error.message}\n`);
      this.emit("event", { type: "failed", message: `Couldn't start Fleet: ${error.message}` });
    });
    child.once("exit", (code, signal) => {
      log.write(`── Fleet exited (code ${code ?? "none"}, signal ${signal ?? "none"})\n`);
      log.end();
      if (this.child === child) this.child = null;
      if (this.stopping) return;
      if (code === IN_USE_EXIT_CODE) {
        this.emit("event", { type: "taken" });
        return;
      }
      this.crashes.push(Date.now());
      const delay = restartDelay(this.crashes, Date.now());
      if (delay === null) {
        this.emit("event", { type: "failed", message: "Fleet keeps stopping unexpectedly." });
        return;
      }
      this.emit("event", { type: "restarting", delayMs: delay, exitCode: code });
      setTimeout(() => {
        if (!this.stopping) this.launch();
      }, delay);
    });

    void ready.then((isUp) => {
      if (this.child !== child) return;
      if (isUp) this.emit("event", { type: "ready", url: this.url, port: this.port });
      else if (child.exitCode === null) {
        this.emit("event", { type: "failed", message: "Fleet started but never answered." });
      }
    });
  }

  private async waitUntilReady(child: ChildProcess): Promise<boolean> {
    const deadline = Date.now() + (this.options.readyTimeoutMs ?? 60_000);
    while (Date.now() < deadline) {
      if (child.exitCode !== null || child.signalCode !== null || this.child !== child) return false;
      if (await isReady(this.url, 1000)) return true;
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    return false;
  }
}

/** Appends to the log, keeping the previous one as `.1` once it passes 10 MB. */
function openLog(file: string): fs.WriteStream {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  try {
    if (fs.statSync(file).size > 10 * 1024 * 1024) fs.renameSync(file, `${file}.1`);
  } catch {
    // No log yet.
  }
  return fs.createWriteStream(file, { flags: "a" });
}

async function settlesWithin(promise: Promise<void>, ms: number): Promise<boolean> {
  let timer: NodeJS.Timeout | undefined;
  const timeout = new Promise<false>((resolve) => {
    timer = setTimeout(() => resolve(false), ms);
  });
  const result = await Promise.race([promise.then(() => true as const), timeout]);
  clearTimeout(timer);
  return result;
}
