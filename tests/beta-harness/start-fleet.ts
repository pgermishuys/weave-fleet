/**
 * Spawn the fleet binary in test-harness mode and wait until it is reachable.
 *
 * Used as a library function; can also be invoked directly via:
 *   bun run start-fleet
 *
 * Returns the base URL once /healthz is 200. The child process keeps running until
 * the script exits or `stopFleet` is called.
 */

import { spawn, type ChildProcess } from "node:child_process";
import { mkdirSync, openSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { setTimeout as sleep } from "node:timers/promises";
import { fileURLToPath } from "node:url";

const HERE = resolve(fileURLToPath(import.meta.url), "..");
const REPO_ROOT = resolve(HERE, "..", "..");
const RUNTIME_DIR = resolve(HERE, ".runtime");
const DATA_DIR = resolve(RUNTIME_DIR, "data");
const SCENARIO_DIR = resolve(RUNTIME_DIR, "scenarios");
const LOG_PATH = resolve(RUNTIME_DIR, "fleet.log");

const DEFAULT_PORT = 5099;
const HEALTHZ_TIMEOUT_MS = 30_000;
const HEALTHZ_POLL_MS = 500;

export interface StartFleetOptions {
  port?: number;
  /** Override the scenarios source dir; defaults to .runtime/scenarios. */
  scenarioDir?: string;
}

export interface RunningFleet {
  baseUrl: string;
  pid: number;
  child: ChildProcess;
  /** Tear down: kills the child and waits for it to exit. */
  stop(): Promise<void>;
}

/** Boot fleet --harness=test, return when /healthz responds 200. */
export async function startFleet(opts: StartFleetOptions = {}): Promise<RunningFleet> {
  const port = opts.port ?? DEFAULT_PORT;
  const scenarioDir = opts.scenarioDir ?? SCENARIO_DIR;

  // Per-run isolation: clean data dir but keep the runtime parent so the log path is stable.
  mkdirSync(RUNTIME_DIR, { recursive: true });
  mkdirSync(DATA_DIR, { recursive: true });
  mkdirSync(scenarioDir, { recursive: true });

  const logFd = openSync(LOG_PATH, "w");
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    FLEET_HARNESS: "test",
    FLEET_BETA_SCENARIO_DIR: scenarioDir,
    // Beta scenarios use OS temp as their working directory; allow it through the
    // workspace-roots gate so POST /api/sessions accepts the directory.
    FLEET_WORKSPACE_ROOTS: process.env.FLEET_WORKSPACE_ROOTS ?? tmpdir(),
    DOTNET_ENVIRONMENT: "Development",
  };

  const child = spawn(
    "dotnet",
    [
      "run",
      "--project",
      "src/WeaveFleet.Api",
      "--no-launch-profile",
      "--",
      "--harness=test",
      "--host",
      "127.0.0.1",
      "--port",
      String(port),
      "--data-dir",
      DATA_DIR,
    ],
    {
      cwd: REPO_ROOT,
      env,
      stdio: ["ignore", logFd, logFd],
      windowsHide: true,
    },
  );

  child.on("exit", (code, signal) => {
    process.stderr.write(
      `[beta-harness] fleet child exited code=${code} signal=${signal}. See ${LOG_PATH}.\n`,
    );
  });

  const baseUrl = `http://127.0.0.1:${port}`;
  await waitForHealthy(baseUrl, HEALTHZ_TIMEOUT_MS);

  return {
    baseUrl,
    pid: child.pid ?? -1,
    child,
    stop: () => stopChild(child),
  };
}

async function waitForHealthy(baseUrl: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown = null;
  while (Date.now() < deadline) {
    try {
      const resp = await fetch(`${baseUrl}/healthz`);
      if (resp.ok) return;
      lastError = `HTTP ${resp.status}`;
    } catch (err) {
      lastError = err;
    }
    await sleep(HEALTHZ_POLL_MS);
  }
  throw new Error(
    `[beta-harness] fleet did not become healthy within ${timeoutMs}ms (last error: ${String(lastError)}). See ${LOG_PATH}.`,
  );
}

async function stopChild(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null) return;
  child.kill("SIGTERM");
  // Give it 5s to shut down cleanly, then SIGKILL.
  await Promise.race([
    new Promise<void>((res) => child.once("exit", () => res())),
    sleep(5_000),
  ]);
  if (child.exitCode === null) child.kill("SIGKILL");
}

// CLI entry point: start fleet, print baseUrl, wait for SIGINT.
const isCliEntry = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isCliEntry) {
  const port = process.env.FLEET_PORT ? Number(process.env.FLEET_PORT) : DEFAULT_PORT;
  const fleet = await startFleet({ port });
  process.stdout.write(`fleet ready at ${fleet.baseUrl} (pid ${fleet.pid})\n`);
  process.stdout.write(`logs: ${LOG_PATH}\n`);
  process.stdout.write("Press Ctrl-C to stop.\n");

  const shutdown = async (): Promise<void> => {
    await fleet.stop();
    process.exit(0);
  };
  process.on("SIGINT", () => void shutdown());
  process.on("SIGTERM", () => void shutdown());

  // Keep the process alive until a signal arrives.
  await new Promise<void>(() => {
    /* never resolves; shutdown happens via SIGINT/SIGTERM */
  });
}

export { LOG_PATH, RUNTIME_DIR, SCENARIO_DIR };
