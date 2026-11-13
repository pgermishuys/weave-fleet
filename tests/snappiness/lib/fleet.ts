/**
 * Start and stop a scratch Fleet in test-harness mode.
 *
 * Every scratch Fleet gets its own profile folder: HOME, USERPROFILE, APPDATA, LOCALAPPDATA and the
 * XDG_* variables all point inside it, and the databases live there too, so it can never read,
 * migrate or delete the real fleet.db. It binds 127.0.0.1 on a port you choose.
 */

import { spawn, type ChildProcess } from "node:child_process";
import { existsSync, mkdirSync, openSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";

export interface ScratchFleet {
  baseUrl: string;
  pid: number;
  home: string;
  projectsDir: string;
  scenarioDir: string;
  stop(): Promise<void>;
}

export interface StartOptions {
  /** A checkout of weave-fleet that has been built (client + `dotnet build -c Release`). */
  repoDir: string;
  /** Scratch folder for this Fleet: profile, databases, logs, projects. */
  scratchDir: string;
  port: number;
}

/** Locate WeaveFleet.Api.dll in a Release build of `repoDir`. */
export function apiDll(repoDir: string): string {
  const binRoot = join(repoDir, "src", "WeaveFleet.Api", "bin", "Release");
  if (!existsSync(binRoot)) throw new Error(`No Release build under ${binRoot}; run build.sh first.`);
  const tfm = readdirSync(binRoot).filter((d) => d.startsWith("net")).sort().pop();
  if (!tfm) throw new Error(`No target framework folder under ${binRoot}.`);
  const dir = join(binRoot, tfm);
  const rid = readdirSync(dir).find((d) => existsSync(join(dir, d, "WeaveFleet.Api.dll")));
  return rid ? join(dir, rid, "WeaveFleet.Api.dll") : join(dir, "WeaveFleet.Api.dll");
}

export function scratchEnv(scratchDir: string, port: number): NodeJS.ProcessEnv {
  const home = resolve(scratchDir, "home");
  const env: NodeJS.ProcessEnv = { ...process.env };
  // Drop anything that could point the scratch Fleet back at the real profile.
  for (const key of Object.keys(env)) {
    if (/^(Fleet__|FLEET_|WEAVE_|XDG_|OPENCODE|CLAUDE)/i.test(key)) delete env[key];
  }
  Object.assign(env, {
    HOME: home,
    USERPROFILE: home,
    APPDATA: join(home, "AppData", "Roaming"),
    LOCALAPPDATA: join(home, "AppData", "Local"),
    XDG_CONFIG_HOME: join(home, ".config"),
    XDG_DATA_HOME: join(home, ".local", "share"),
    XDG_STATE_HOME: join(home, ".local", "state"),
    XDG_CACHE_HOME: join(home, ".cache"),
    TEMP: join(home, "tmp"),
    TMP: join(home, "tmp"),
    FLEET_HARNESS: "test",
    FLEET_BETA_SCENARIO_DIR: join(scratchDir, "scenarios"),
    FLEET_WORKSPACE_ROOTS: join(home, "proj"),
    Fleet__Host: "127.0.0.1",
    Fleet__Port: String(port),
    Fleet__DatabasePath: join(home, "data", "fleet.db"),
    Fleet__AnalyticsDatabasePath: join(home, "data", "fleet-analytics.db"),
    ASPNETCORE_ENVIRONMENT: "Production",
    DOTNET_ENVIRONMENT: "Production",
    Logging__LogLevel__Default: "Warning",
  });
  return env;
}

export async function startFleet(opts: StartOptions): Promise<ScratchFleet> {
  if ([6262].includes(opts.port)) throw new Error("Refusing the installed Fleet's port.");
  const scratchDir = resolve(opts.scratchDir);
  const env = scratchEnv(scratchDir, opts.port);
  const home = env.HOME!;
  for (const dir of [home, env.APPDATA!, env.LOCALAPPDATA!, env.XDG_CONFIG_HOME!, env.XDG_DATA_HOME!, env.XDG_STATE_HOME!,
    env.XDG_CACHE_HOME!, env.TEMP!, join(home, "data"), join(home, "proj"), env.FLEET_BETA_SCENARIO_DIR!]) {
    mkdirSync(dir, { recursive: true });
  }

  const dll = apiDll(opts.repoDir);
  const log = openSync(join(scratchDir, "fleet.log"), "a");
  const child: ChildProcess = spawn(
    "dotnet",
    [dll, "--harness=test", "--host", "127.0.0.1", "--port", String(opts.port)],
    { cwd: join(opts.repoDir, "src", "WeaveFleet.Api"), env, stdio: ["pipe", log, log], windowsHide: true },
  );

  const baseUrl = `http://127.0.0.1:${opts.port}`;
  const deadline = Date.now() + 60_000;
  let last = "";
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Fleet exited with ${child.exitCode}; see ${join(scratchDir, "fleet.log")}`);
    try {
      const r = await fetch(`${baseUrl}/healthz`);
      if (r.ok) break;
      last = `HTTP ${r.status}`;
    } catch (e) {
      last = String(e);
    }
    await new Promise((r) => setTimeout(r, 300));
  }
  if (Date.now() >= deadline) throw new Error(`Fleet not healthy: ${last}`);

  return {
    baseUrl,
    pid: child.pid ?? -1,
    home,
    projectsDir: join(home, "proj"),
    scenarioDir: env.FLEET_BETA_SCENARIO_DIR!,
    stop: async () => {
      if (child.exitCode !== null) return;
      child.kill();
      await Promise.race([new Promise((r) => child.once("exit", r)), new Promise((r) => setTimeout(r, 5000))]);
      if (child.exitCode === null && child.pid) {
        spawn("taskkill", ["/F", "/T", "/PID", String(child.pid)], { windowsHide: true });
      }
    },
  };
}
